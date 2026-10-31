import { and, eq, getTableColumns, sql, type SQL } from 'drizzle-orm';
import { drizzle } from 'drizzle-orm/d1';
import { type AnySQLiteTable } from 'drizzle-orm/sqlite-core';
import * as schema from './schema.ts';
import { apiTime, exactInt64, utcTicks } from './time.ts';
import { type Database, type Statement } from './database.ts';
import { addressSnapshot, cardSnapshot, requestSnapshot, messageSnapshot } from './wire-snapshots.ts';

const ticks=(value:string|null|undefined)=>value==null?null:utcTicks(value);
// rate_limits policy per counter kind; every kind except public-rate counts fixed UTC hours.
const ratePolicies:Record<string,string>={'registration-rate':'registration','public-rate':'public-read','recovery-rate':'key-recovery'};
const pair=(id:string)=>JSON.parse(id) as [string,string];
const selectColumns=(table:AnySQLiteTable)=>Object.fromEntries(Object.entries(getTableColumns(table)).map(([name,column])=>[
    name,column.getSQLType()==='integer'&&column.dataType==='custom'?sql`CAST(${column} AS TEXT)`.as(name):column
]));

// Wire snapshots are reconstructed at this boundary. Business state is stored
// only in the dedicated tables; message bytes never pass through stored JSON.
export class RecordStore {
    readonly orm;
    readonly db:Database;readonly writes:Statement[];readonly now:bigint;
    constructor(db:Database,writes:Statement[],now:bigint){this.db=db;this.writes=writes;this.now=now;this.orm=drizzle(db as Parameters<typeof drizzle>[0]);}
    async select(table:AnySQLiteTable,where:SQL):Promise<any|null>{
        const query=this.orm.select(selectColumns(table)).from(table).where(where).limit(1).toSQL();
        return this.db.prepare(query.sql).bind(...query.params as (string|number|null)[]).first();
    }
    async rows(table:AnySQLiteTable,condition:string,args:(string|number)[],suffix:string):Promise<any[]>{
        const query=this.orm.select(selectColumns(table)).from(table).toSQL();
        return (await this.db.prepare(query.sql+' WHERE '+condition+' '+suffix).bind(...args).all<any>()).results;
    }
    messageQuery():string{
        const d=selectColumns(schema.messageDelivery);
        delete d.created_at;delete d.updated_at;
        return this.orm.select({...selectColumns(schema.mailboxRecords),...d}).from(schema.mailboxRecords)
            .innerJoin(schema.messageDelivery,eq(schema.mailboxRecords.seq,schema.messageDelivery.message_seq)).toSQL().sql;
    }
    stage(query:{toSQL():{sql:string;params:unknown[]}}):void{
        const compiled=query.toSQL();this.writes.push(this.db.prepare(compiled.sql).bind(...compiled.params as (string|number|null|ArrayBuffer)[]));
    }
    upsert(table:AnySQLiteTable,value:Record<string,any>,keys:string[],immutable:string[]=[]):void {
        const columns=getTableColumns(table);
        const insert:Record<string,any>={updated_at:this.now,...value};
        if(!columns.updated_at)delete insert.updated_at;
        const updates=Object.fromEntries(Object.keys(insert).filter(key=>!keys.includes(key)&&key!=='created_at'&&!immutable.includes(key)).map(key=>[key,sql.raw('excluded."'+key+'"')]));
        this.stage(this.orm.insert(table).values(insert).onConflictDoUpdate({target:keys.map(key=>columns[key]),set:updates}));
    }
    sequence(table:AnySQLiteTable,where:SQL):SQL {
        const column=getTableColumns(table).seq;
        return sql`COALESCE((SELECT ${column} FROM ${table} WHERE ${where}),(SELECT CASE WHEN value<9223372036854775807 THEN value+1 ELSE abs(-9223372036854775808) END FROM counters WHERE name='record-seq'))`;
    }
    advanceSequence(table:AnySQLiteTable,where:SQL):void {
        const column=getTableColumns(table).seq;
        this.stage(this.orm.update(schema.counters).set({value:sql`max(value,(SELECT ${column} FROM ${table} WHERE ${where}))`,updated_at:this.now}).where(and(eq(schema.counters.name,'record-seq'),sql`value<(SELECT ${column} FROM ${table} WHERE ${where})`)));
    }
    async get(kind:string,owner:string,id:string):Promise<any|null>{
        switch(kind){
        case 'account':{
            const r=await this.select(schema.accounts,eq(schema.accounts.account_id,id));
            return r&&{accountId:r.account_id,humanId:r.human_id,displayName:r.display_name,description:r.description};
        }
        case 'card':{
            const a=await this.select(schema.accountAddresses,eq(schema.accountAddresses.address_id,id));
            if(a)return addressSnapshot(a);
            const c=await this.select(schema.contactCards,eq(schema.contactCards.card_id,id));
            return c&&cardSnapshot(c);
        }
        case 'request':{
            const r=await this.select(schema.contactRequests,and(eq(schema.contactRequests.requester_account_id,owner),eq(schema.contactRequests.request_id,id))!);
            return r&&requestSnapshot(r);
        }
        case 'channel':{
            const [low,high]=pair(id),r=await this.select(schema.legacyChannels,and(eq(schema.legacyChannels.low_account_id,low),eq(schema.legacyChannels.high_account_id,high))!);
            return r&&{low:r.low_account_id,high:r.high_account_id,generation:exactInt64(r.generation),blockedLow:!!r.blocked_low,blockedHigh:!!r.blocked_high,currentRequest:r.current_request_id===null?null:{requesterMailboxId:r.current_requester_account_id,requestId:r.current_request_id},updatedAt:apiTime(r.updated_at)};
        }
        case 'message':{
            const r=await this.select(schema.mailboxRecords,and(eq(schema.mailboxRecords.sender_account_id,owner),eq(schema.mailboxRecords.message_id,id))!);
            if(!r)return null;
            const d=await this.select(schema.messageDelivery,eq(schema.messageDelivery.message_seq,exactInt64(r.seq)));
            if(!d)throw Error('Message delivery state is missing.');
            return messageSnapshot(r,d);
        }
        case 'registration-rate':case 'public-rate':case 'recovery-rate':{
            const r=await this.select(schema.rateLimits,and(eq(schema.rateLimits.policy,ratePolicies[kind]),eq(schema.rateLimits.bucket_key,id))!);
            return r&&(kind!=='public-rate'?{hour:Number(exactInt64(r.window_start)/36000000000n),count:r.count}:{at:Number(exactInt64(r.window_start)/10000n),count:r.count});
        }
        case 'stream':{
            const r=await this.select(schema.streamLeases,and(eq(schema.streamLeases.account_id,owner),eq(schema.streamLeases.lease_id,id))!);
            return r&&{expires:Number(exactInt64(r.expires_at)/10000n)};
        }
        case 'stream-progress':{
            const r=await this.select(schema.streamCursors,eq(schema.streamCursors.account_id,owner));return r&&{cursor:r.cursor,version:r.version};
        }
        case 'counter':{
            const r=await this.select(schema.counters,eq(schema.counters.name,id));return r&&{value:r.value};
        }
        case 'maintenance':{
            const r=await this.select(schema.maintenanceState,eq(schema.maintenanceState.name,id));return r&&{at:Number(exactInt64(r.last_run_at)/10000n)};
        }
        default:throw Error('Unknown record kind.');
        }
    }
    put(kind:string,owner:string,id:string,v:any):void{
        switch(kind){
        case 'account':this.upsert(schema.accounts,{account_id:id,human_id:v.humanId??null,display_name:v.displayName,description:v.description,created_at:this.now},['account_id'],['human_id']);return;
        case 'card':{
            const a=v._address,table=a?schema.accountAddresses:schema.contactCards,column=a?schema.accountAddresses.address_id:schema.contactCards.card_id,where=eq(column,id);
            const common={seq:this.sequence(table,where),created_at:ticks(v.createdAt),expires_at:ticks(v.expiresAt),revoked_at:ticks(v.revokedAt),public_seq:v._publicSequence===null?null:exactInt64(v._publicSequence)};
            if(a)this.upsert(table,{...common,address_id:id,account_id:v.ownerMailboxId,label:a.label,is_public:a.isPublic?1:0,effective_from:ticks(a.effectiveFrom),effective_to:ticks(a.effectiveTo),direct_uri:a.directUri,legacy_relay_endpoint:v.methods.find((m:any)=>m.methodId==='relay').endpoint},['address_id'],['seq','account_id','legacy_relay_endpoint']);
            else this.upsert(table,{...common,card_id:id,owner_account_id:v.ownerMailboxId,visibility:v.visibility,audience_hint:v.audienceHint,methods:JSON.stringify(v.methods)},['card_id'],['seq','owner_account_id']);
            this.advanceSequence(table,where);return;
        }
        case 'request':{
            const t=schema.contactRequests,s=v.summary,where=and(eq(t.requester_account_id,owner),eq(t.request_id,id))!;
            this.upsert(t,{requester_account_id:owner,request_id:id,seq:this.sequence(t,where),recipient_account_id:s.recipientMailboxId,request_version:v.requestVersion??1,requester_address_id:v.requesterAddressId??null,recipient_address_id:v.recipientAddressId??null,target_card_id:v.targetCardId??null,
                method_id:s.methodId,legacy_generation:s.generation,status:s.status,created_at:ticks(s.createdAt),expires_at:ticks(s.expiresAt),request_message_id:s.requestMessageId,reply_message_id:s.replyMessageId,fingerprint:v.fingerprint,decision_fingerprint:v.decisionFingerprint,terminal_at:ticks(v.terminalAt),updated_at:ticks(v.updatedAt)},['requester_account_id','request_id'],['seq','recipient_account_id','request_version','requester_address_id','recipient_address_id','target_card_id','fingerprint','expires_at','request_message_id']);
            this.advanceSequence(t,where);return;
        }
        case 'channel':this.upsert(schema.legacyChannels,{low_account_id:v.low,high_account_id:v.high,generation:v.generation,blocked_low:v.blockedLow?1:0,blocked_high:v.blockedHigh?1:0,current_requester_account_id:v.currentRequest?.requesterMailboxId??null,current_request_id:v.currentRequest?.requestId??null,created_at:this.now,updated_at:ticks(v.updatedAt)},['low_account_id','high_account_id']);return;
        case 'message':{
            const t=schema.mailboxRecords,r=v.receipt,where=and(eq(t.sender_account_id,owner),eq(t.message_id,id))!,bytes=v.payload===null?null:Uint8Array.from(atob(v.payload.bytes),(c:string)=>c.charCodeAt(0));
            this.upsert(t,{seq:this.sequence(t,where),message_id:id,sender_account_id:owner,recipient_account_id:r.recipientMailboxId,payload:bytes,payload_size:v.size,payload_encoding:v.payload?.payloadEncoding??null,created_at:ticks(v.payload?.createdAt),expires_at:ticks(r.expiresAt)},['sender_account_id','message_id'],['seq','recipient_account_id','payload_encoding','expires_at']);
            this.advanceSequence(t,where);
            this.upsert(schema.messageDelivery,{message_seq:sql`(SELECT ${t.seq} FROM ${t} WHERE ${where})`,state:r.state,terminal_at:ticks(r.terminalAt),protocol_version:v.version,purpose:v.purpose,legacy_generation:v.generation,connection_token:v.connectionToken??null,
                requester_account_id:v.request?.requesterMailboxId??null,request_id:v.request?.requestId??null,fingerprint:v.fingerprint,fingerprint_scheme:v.fingerprintScheme??'legacy-envelope-v1',created_at:this.now},['message_seq'],['protocol_version','purpose','legacy_generation','connection_token','requester_account_id','request_id','fingerprint','fingerprint_scheme']);
            if(bytes===null)this.writes.push(this.db.prepare("UPDATE mailbox_records_legacy SET data=json_set(data,'$.payload',NULL,'$.size',0),payload_cleared_at=? WHERE kind='message' AND owner=? AND id=? AND json_extract(data,'$.payload') IS NOT NULL").bind(this.now.toString(),owner,id));
            return;
        }
        case 'registration-rate':case 'public-rate':case 'recovery-rate':{
            const t=schema.rateLimits,policy=ratePolicies[kind],where=and(eq(t.policy,policy),eq(t.bucket_key,id))!;
            this.upsert(t,{seq:this.sequence(t,where),policy,bucket_key:id,window_start:kind!=='public-rate'?BigInt(v.hour)*36000000000n:BigInt(v.at)*10000n,count:v.count,created_at:this.now},['policy','bucket_key'],['seq']);this.advanceSequence(t,where);return;
        }
        case 'stream':this.upsert(schema.streamLeases,{account_id:owner,lease_id:id,expires_at:BigInt(v.expires)*10000n,created_at:this.now},['account_id','lease_id']);return;
        case 'stream-progress':this.upsert(schema.streamCursors,{account_id:owner,cursor:v.cursor,version:v.version,created_at:this.now},['account_id']);return;
        case 'counter':this.upsert(schema.counters,{name:id,value:exactInt64(v.value),created_at:this.now},['name']);return;
        case 'maintenance':this.upsert(schema.maintenanceState,{name:id,last_run_at:BigInt(v.at)*10000n,created_at:this.now},['name']);return;
        default:throw Error('Unknown record kind.');
        }
    }
}
