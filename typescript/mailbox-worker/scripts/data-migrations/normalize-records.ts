import { createHash } from 'node:crypto';
import { setImmediate as yieldToIo } from 'node:timers/promises';
import { readFile } from 'node:fs/promises';
import { getTableColumns, getTableName } from 'drizzle-orm';
import * as schema from '../../src/schema.ts';
import { exactInt64, nowUtc } from '../../src/time.ts';
import { convertLegacyRecords } from './legacy-records.mjs';
import { createMissingSchema, latestSchemaStatements } from '../latest-schema.mjs';

export const scriptId='normalize-mailbox-records';
const tables=new Map(Object.values(schema).map(table=>[getTableName(table),table]));
const hash=value=>createHash('sha256').update(value).digest('hex');
const canonical=value=>JSON.stringify(value,(_,v)=>typeof v==='bigint'?v.toString():v instanceof Uint8Array?{hex:Buffer.from(v).toString('hex')}:
    v&&typeof v==='object'&&!Array.isArray(v)?Object.fromEntries(Object.keys(v).sort().map(key=>[key,v[key]])):v);
async function checksum() {
    return hash((await Promise.all(['./normalize-records.mjs','./legacy-records.mjs','../../src/time.ts','../../src/credentials.ts'].map(path=>readFile(new URL(path,import.meta.url),'utf8')))).join('\n'));
}
async function ledger(db) {
    return db.prepare('SELECT checksum,CAST(created_at AS TEXT) created_at,CAST(completed_at AS TEXT) completed_at FROM data_migrations WHERE script_id=?').bind(scriptId).first();
}
async function history(db) {
    const exists=await db.prepare("SELECT 1 found FROM sqlite_master WHERE type='table' AND name='d1_migrations'").first();
    if(!exists)return null;
    return hash(canonical((await db.prepare('SELECT * FROM d1_migrations ORDER BY id').all()).results));
}
async function readTarget(db,name) {
    const columns=getTableColumns(tables.get(name));
    const sql=Object.entries(columns).map(([key,c])=>c.getSQLType()==='integer'&&c.dataType==='custom'?`CAST("${key}" AS TEXT) "${key}"`:`"${key}"`).join(',');
    const rows=[];let after=null;
    // A BLOB is expanded by the binding transport; keep each response bounded.
    for(;;){
        const page=(await db.prepare(`SELECT CAST(rowid AS TEXT) __rowid,${sql} FROM "${name}" WHERE (? IS NULL OR rowid>CAST(? AS INTEGER)) ORDER BY "${name}".rowid LIMIT 16`).bind(after,after).all()).results;
        for(const {__rowid,...row} of page)rows.push(Object.fromEntries(Object.entries(row).map(([key,value])=>[
            key,value===null?null:columns[key].getSQLType()==='integer'&&columns[key].dataType==='custom'?exactInt64(value):columns[key].getSQLType()==='blob'?Buffer.from(value):value
        ])));
        if(page.length<16)break;after=page.at(-1).__rowid;
    }
    return rows;
}
function rowIdentity(name,row) {
    const keys={accounts:['account_id'],account_addresses:['address_id'],contact_cards:['card_id'],contact_requests:['requester_account_id','request_id'],contacts:['owner_account_id','to_account_id'],
        mailbox_records:['seq'],message_delivery:['message_seq'],legacy_channels:['low_account_id','high_account_id'],rate_limits:['seq'],stream_leases:['account_id','lease_id'],stream_cursors:['account_id'],counters:['name'],maintenance_state:['name']}[name];
    return canonical(keys.map(key=>row[key]));
}
async function migrationBatch(db,statements){
    // Wrangler's Node binding proxy has synchronous prepare/bind calls. Yield
    // to its connection timers before dispatch; never retry an unknown write.
    await yieldToIo();return db.batch(statements);
}
async function writeRows(db,name,rows) {
    const existing=new Map((await readTarget(db,name)).map(row=>[rowIdentity(name,row),canonical(row)]));
    const statements=[];
    for(const row of rows) {
        const found=existing.get(rowIdentity(name,row));
        if(found!==undefined){if(found!==canonical(row))throw Error('Conflicting existing conversion row in '+name+'.');continue;}
        const entries=Object.entries(row),values=entries.map(([,v])=>v instanceof Uint8Array?Buffer.from(v).toString('hex'):typeof v==='bigint'?v.toString():v);
        const parameters=entries.map(([,v])=>v instanceof Uint8Array?'unhex(?)':'?').join(',');
        statements.push(db.prepare(`INSERT INTO "${name}"(${entries.map(([key])=>'"'+key+'"').join(',')}) VALUES(${parameters})`).bind(...values));
        // Contact pairs are adjacent; an even chunk keeps each pair atomic.
        if(statements.length===32){await migrationBatch(db,statements);statements.length=0;}
    }
    if(statements.length)await migrationBatch(db,statements);
}
async function source(db) {
    const rows=[];let after=null;
    for(;;){
        const page=(await db.prepare('SELECT CAST(seq AS TEXT) seq,kind,owner,id,data FROM mailbox_records_legacy WHERE (? IS NULL OR seq>CAST(? AS INTEGER)) ORDER BY mailbox_records_legacy.seq LIMIT 16').bind(after,after).all()).results;
        rows.push(...page);if(page.length<16)break;after=page.at(-1).seq;
    }
    const highWater=exactInt64(await db.prepare("SELECT CAST(seq AS TEXT) value FROM sqlite_sequence WHERE name='mailbox_records_legacy'").first('value')??'0');
    return {rows,highWater,digest:hash(canonical(rows))};
}

// The caller routes all HTTP and scheduled work to maintenance before this runs.
// The temporary guard trigger additionally rejects already-in-flight old writers.
// Errors deliberately leave the gate installed. Only verified completion opens it.
export async function normalizeRecords(db,{now=nowUtc(),checkpoint=async()=>{}}={}) {
    const beforeHistory=await history(db),digest=await checksum();
    const ddl=await latestSchemaStatements();
    await db.prepare(ddl.find(sql=>/^CREATE TABLE `data_migrations`/.test(sql)).replace('CREATE TABLE','CREATE TABLE IF NOT EXISTS')).run();
    let state=await ledger(db);
    if(state){
        if(state.checksum!==digest)throw Error('Data migration checksum changed; conversion is paused.');
        if(state.completed_at!==null)return {alreadyCompleted:true,historyDigest:beforeHistory};
    } else {
        await migrationBatch(db,[
            db.prepare('INSERT INTO data_migrations(script_id,checksum,created_at,updated_at) VALUES(?,?,?,?)').bind(scriptId,digest,now.toString(),now.toString()),
            db.prepare("CREATE TRIGGER data_migration_write_gate BEFORE INSERT ON mailbox_guard BEGIN SELECT RAISE(ABORT,'data_migration_in_progress'); END"),
            db.prepare('UPDATE mailbox_revision SET token=? WHERE singleton=1').bind(crypto.randomUUID())
        ]);
        state=await ledger(db);
    }
    const gate=await db.prepare("SELECT 1 found FROM sqlite_master WHERE type='trigger' AND name='data_migration_write_gate'").first();
    if(!gate)throw Error('Data migration write gate is missing; conversion is paused.');
    await checkpoint('gated');
    const archived=await db.prepare("SELECT 1 found FROM sqlite_master WHERE type='table' AND name='mailbox_records_legacy'").first();
    if(!archived){
        const guard=await db.prepare('SELECT count(*) count FROM mailbox_guard').first('count');
        if(guard!==0)throw Error('Transaction guard is not empty; conversion is paused.');
        await migrationBatch(db,[
            db.prepare('ALTER TABLE mailbox_records RENAME TO mailbox_records_legacy'),
            db.prepare('ALTER TABLE mailbox_records_legacy ADD COLUMN archived_at INTEGER NOT NULL DEFAULT 0'),
            db.prepare('ALTER TABLE mailbox_records_legacy ADD COLUMN payload_cleared_at INTEGER'),
            db.prepare('UPDATE mailbox_records_legacy SET archived_at=?').bind(now.toString()),
            db.prepare('ALTER TABLE mailbox_revision ADD COLUMN created_at INTEGER'),
            db.prepare('ALTER TABLE mailbox_revision ADD COLUMN updated_at INTEGER'),
            db.prepare('ALTER TABLE mailbox_guard ADD COLUMN created_at INTEGER NOT NULL'),
            db.prepare('ALTER TABLE mailbox_guard ADD COLUMN updated_at INTEGER NOT NULL')
        ]);
    }
    await createMissingSchema(db);
    await checkpoint('archived');
    const before=await source(db),converted=convertLegacyRecords(before.rows,{now,highWater:before.highWater,contacts:await readTarget(db,'contacts'),recordCounter:(await readTarget(db,'counters')).find(row=>row.name==='record-seq')});
    for(const [name,rows] of Object.entries(converted)){await writeRows(db,name,rows);await checkpoint(name);}
    const counts={};
    for(const [name,rows] of Object.entries(converted)){
        const actual=await readTarget(db,name);
        const ordered=values=>values.map(row=>canonical(row)).sort();
        if(canonical(ordered(actual))!==canonical(ordered(rows)))throw Error('Conversion verification failed in '+name+'.');
        counts[name]=actual.length;
    }
    if((await db.prepare('PRAGMA foreign_key_check').all()).results.length)throw Error('Foreign-key validation failed; conversion is paused.');
    if((await source(db)).digest!==before.digest)throw Error('Archived source changed during conversion; conversion is paused.');
    if(await history(db)!==beforeHistory)throw Error('d1_migrations history changed; conversion is paused.');
    await checkpoint('verified');
    const completed=nowUtc().toString();
    await migrationBatch(db,[
        db.prepare('UPDATE data_migrations SET updated_at=?,completed_at=? WHERE script_id=? AND checksum=? AND completed_at IS NULL').bind(completed,completed,scriptId,digest),
        db.prepare('UPDATE mailbox_revision SET token=?,updated_at=? WHERE singleton=1').bind(crypto.randomUUID(),completed),
        db.prepare('DROP TRIGGER data_migration_write_gate')
    ]);
    return {alreadyCompleted:false,counts,sourceDigest:before.digest,historyDigest:beforeHistory,highWater:before.highWater.toString()};
}
