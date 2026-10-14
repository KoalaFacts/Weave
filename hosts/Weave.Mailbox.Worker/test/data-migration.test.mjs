import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Miniflare, convertV4MiniflareOptions } from 'miniflare';
import { readFile } from 'node:fs/promises';
import { setImmediate as yieldToIo } from 'node:timers/promises';
import { normalizeRecords } from '../scripts/data-migrations/normalize-records.mjs';
import { convertLegacyRecords, payloadBytes } from '../scripts/data-migrations/legacy-records.mjs';
import { exactInt64, utcTicks, isoUtc } from '../src/time.ts';

const created='2026-10-14T10:00:00.1234567Z',expires='2026-10-14T11:00:00.1234567Z',now=utcTicks('2026-10-14T10:30:00.7654321Z');
const highWater=9007199254740999n,generation=9007199254740993n;
const parameters={algorithm:'SHA-256',inputEncoding:'utf-8',digestEncoding:'hex-lower',normalization:'none',salt:null,rounds:1};
const stringify=value=>JSON.stringify(value,(_,v)=>typeof v==='bigint'?'BIGINT:'+v:v).replace(/"BIGINT:(\d+)"/g,'$1');
function fixture() {
    const rows=[];
    const add=(kind,owner,id,value)=>rows.push({seq:String(rows.length+1),kind,owner,id,data:stringify(value)});
    for(const id of ['a','b'])add('account','',id,{accountId:id,controlHash:id.repeat(64),controlHashScheme:'sha256-utf8-v1',controlHashParameters:parameters,displayName:id,description:null});
    add('card','','address-b',{cardId:'address-b',ownerMailboxId:'b',visibility:'public',createdAt:created,expiresAt:expires,revokedAt:null,methods:[{methodId:'relay',transport:'relay',endpoint:'https://example.invalid/inbox/b'}],_address:{isPublic:true,label:'synthetic',effectiveFrom:created,effectiveTo:null,directUri:null},_deleted:false,_publicSequence:'3'});
    add('card','','ordinary-b',{cardId:'ordinary-b',ownerMailboxId:'b',visibility:'unlisted',createdAt:created,expiresAt:null,revokedAt:null,audienceHint:null,methods:[{methodId:'direct',transport:'direct',endpoint:'https://example.invalid'}],_publicSequence:null});
    add('request','a','request-a',{summary:{requestId:'request-a',requesterMailboxId:'a',recipientMailboxId:'b',cardId:'address-b',methodId:'relay',generation,status:'accepted',createdAt:created,expiresAt:expires,requestMessageId:'intro-a',replyMessageId:'reply-b'},fingerprint:'legacy-request-digest',decisionFingerprint:'legacy-decision-digest',updatedAt:created,terminalAt:null});
    add('channel','',JSON.stringify(['a','b']),{low:'a',high:'b',generation,blockedLow:false,blockedHigh:false,currentRequest:{requesterMailboxId:'a',requestId:'request-a'},updatedAt:created});
    const bytes=Buffer.alloc(65536);for(let i=0;i<bytes.length;i++)bytes[i]=i%256;
    for(const [id,data] of [['binary',bytes],['empty',Buffer.alloc(0)],['cleared',null]])add('message','a',id,{
        receipt:{messageId:id,senderMailboxId:'a',recipientMailboxId:'b',state:data===null?'acknowledged':'pending',expiresAt:expires,terminalAt:data===null?created:null},
        generation,version:1,payload:data===null?null:{messageId:id,bytes:data.toString('base64'),payloadEncoding:'application/octet-stream',createdAt:created,expiresAt:expires},
        size:data?.length??0,expiresTicks:utcTicks(expires).toString(),fingerprint:'unchanged-'+id,purpose:0,request:null});
    add('registration-rate','','global',{hour:497770,count:4});
    add('public-rate','','global',{at:1791972000000,count:7});
    add('stream','b','lease-b',{expires:1791972060000});
    add('stream-progress','b','cursor',{cursor:'opaque-cursor',version:'opaque-version'});
    add('counter','','public-cards',{value:'3'});
    add('maintenance','','sweep',{at:1791972000000});
    return {rows,bytes};
}
async function database() {
    const mf=new Miniflare(convertV4MiniflareOptions({modules:true,script:'export default {fetch(){return new Response("migration fixture")}}',compatibilityDate:'2026-07-01',d1Databases:['db']}));
    const db=await mf.getD1Database('db');
    for(const sql of (await readFile('test/fixtures/legacy-mailbox.sql','utf8')).split(';').filter(x=>x.trim()))await db.prepare(sql).run();
    await db.prepare("CREATE TABLE d1_migrations(id INTEGER PRIMARY KEY,name TEXT NOT NULL,applied_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP)").run();
    await db.prepare("INSERT INTO d1_migrations(id,name,applied_at) VALUES(1,'0001_mailbox.sql','2026-10-07 00:00:00')").run();
    const data=fixture();
    await db.batch(data.rows.map(row=>db.prepare('INSERT INTO mailbox_records(seq,kind,owner,id,data) VALUES(?,?,?,?,?)').bind(row.seq,row.kind,row.owner,row.id,row.data)));
    await db.prepare('UPDATE sqlite_sequence SET seq=? WHERE name=?').bind(highWater.toString(),'mailbox_records').run();
    return {db,...data,close:()=>mf.dispose()};
}

test('real D1 conversion preserves IDs, exact times, BLOB bytes, high-water, archive and d1 history',async()=>{
    const h=await database();
    try {
        const before=(await h.db.prepare('SELECT * FROM d1_migrations').all()).results;
        const result=await normalizeRecords(h.db,{now});
        assert.equal(result.highWater,highWater.toString());assert.equal(result.counts.accounts,2);assert.equal(result.counts.account_addresses,1);assert.equal(result.counts.contact_cards,1);assert.equal(result.counts.contacts,2);
        assert.deepEqual((await h.db.prepare('SELECT * FROM d1_migrations').all()).results,before);
        const archive=(await h.db.prepare('SELECT CAST(seq AS TEXT) seq,kind,owner,id,data FROM mailbox_records_legacy ORDER BY mailbox_records_legacy.seq').all()).results;
        assert.equal(archive.length,h.rows.length);
        for(let i=0;i<archive.length;i++)assert.ok(JSON.stringify(archive[i])===JSON.stringify(h.rows[i]),'Archived row differs at sequence '+h.rows[i].seq);
        assert.equal(await h.db.prepare('SELECT CAST(archived_at AS TEXT) value FROM mailbox_records_legacy LIMIT 1').first('value'),now.toString());
        assert.equal(await h.db.prepare("SELECT CAST(value AS TEXT) value FROM counters WHERE name='record-seq'").first('value'),highWater.toString());
        const record=await h.db.prepare("SELECT payload,typeof(payload) type,CAST(created_at AS TEXT) created_at FROM mailbox_records WHERE message_id='binary'").first();
        assert.equal(record.type,'blob');assert.equal(record.created_at,utcTicks(created).toString());assert.deepEqual(Buffer.from(record.payload),h.bytes);
        assert.deepEqual((await h.db.prepare("SELECT message_id,typeof(payload) type,created_at IS NULL unknown_time FROM mailbox_records WHERE message_id IN ('empty','cleared') ORDER BY message_id").all()).results,[{message_id:'cleared',type:'null',unknown_time:1},{message_id:'empty',type:'blob',unknown_time:0}]);
        const request=await h.db.prepare('SELECT requester_address_id,request_version,CAST(legacy_generation AS TEXT) generation,fingerprint FROM contact_requests').first();
        assert.deepEqual(request,{requester_address_id:null,request_version:1,generation:generation.toString(),fingerprint:'legacy-request-digest'});
        assert.equal(await h.db.prepare("SELECT count(DISTINCT connection_token) count FROM contacts").first('count'),1);
        assert.equal(await h.db.prepare("SELECT count(*) count FROM sqlite_master WHERE type IN ('view','trigger')").first('count'),0);
        assert.equal((await normalizeRecords(h.db,{now})).alreadyCompleted,true);
    } finally {await h.close();}
});

test('interruption gates old in-flight writes and resumes without duplicating or losing converted data',async()=>{
    const h=await database();
    try {
        await assert.rejects(normalizeRecords(h.db,{now,checkpoint:async phase=>{if(phase==='gated'){
            await assert.rejects(h.db.batch([h.db.prepare("UPDATE mailbox_revision SET token='inflight'"),h.db.prepare('INSERT INTO mailbox_guard(ok) VALUES(1)')]),/data_migration_in_progress/);
            assert.notEqual(await h.db.prepare('SELECT token FROM mailbox_revision').first('token'),'inflight');
        }if(phase==='contacts')throw Error('synthetic interruption');}}),/synthetic interruption/);
        assert.equal(await h.db.prepare("SELECT count(*) count FROM contacts").first('count'),2);
        const token=await h.db.prepare('SELECT connection_token FROM contacts LIMIT 1').first('connection_token');
        const result=await normalizeRecords(h.db,{now:now+10000000n});
        assert.equal(result.counts.contacts,2);assert.equal(await h.db.prepare('SELECT connection_token FROM contacts LIMIT 1').first('connection_token'),token);
        assert.equal(await h.db.prepare("SELECT CAST(created_at AS TEXT) value FROM counters WHERE name='record-seq'").first('value'),(now+10000000n).toString());
    } finally {await h.close();}
});

test('invalid or orphan source fails closed without skipping records or releasing migration gate',async()=>{
    const h=await database();
    try {
        await h.db.prepare("UPDATE mailbox_records SET data=json_set(data,'$.controlHashParameters.rounds',2) WHERE kind='account' AND id='a'").run();
        await assert.rejects(normalizeRecords(h.db,{now}),/Invalid legacy account at sequence 1/);
        assert.equal(await h.db.prepare("SELECT count(*) count FROM sqlite_master WHERE type='trigger' AND name='data_migration_write_gate'").first('count'),1);
        assert.equal(await h.db.prepare('SELECT count(*) count FROM mailbox_records_legacy').first('count'),h.rows.length);
        assert.equal(await h.db.prepare('SELECT completed_at FROM data_migrations').first('completed_at'),null);
    } finally {await h.close();}
});

test('archive scan crosses numeric sequence pages without lexical skips',async()=>{
    const h=await database();
    try {
        const statements=[];
        for(let i=16;i<=270;i++)statements.push(h.db.prepare("INSERT INTO mailbox_records(seq,kind,owner,id,data) VALUES(?,'stream','b',?,?)").bind(i,'page-lease-'+i,JSON.stringify({expires:1791972060000})));
        // Preparing hundreds of proxy statements is synchronous; let the Node
        // connection pool observe idle socket closure before dispatching writes.
        await yieldToIo();
        for(let i=0;i<statements.length;i+=32)await h.db.batch(statements.slice(i,i+32));
        const result=await normalizeRecords(h.db,{now});
        assert.equal(result.counts.stream_leases,256);
        assert.equal(await h.db.prepare('SELECT count(*) count FROM mailbox_records_legacy').first('count'),270);
    } finally {await h.close();}
});

test('conversion refuses unknown kinds, malformed bytes and precision loss',()=>{
    assert.throws(()=>convertLegacyRecords([{seq:'1',kind:'unknown',owner:'',id:'x',data:'{}'}],{now,highWater}),/Invalid legacy unknown/);
    assert.throws(()=>payloadBytes({bytes:Buffer.alloc(65537).toString('base64')}),/64 KiB/);
    assert.throws(()=>payloadBytes({bytes:'not base64'}),/Invalid legacy base64/);
    assert.throws(()=>exactInt64(Number(highWater)),/exact decimal/);
    assert.throws(()=>exactInt64('9223372036854775808'),/int64/);
    for(const value of [created,'1969-12-31T23:59:59.9999999Z','0001-01-01T00:00:00.0000001Z','2024-02-29T12:03:04.0000001+05:30'])assert.equal(utcTicks(isoUtc(utcTicks(value))),utcTicks(value));
    assert.throws(()=>utcTicks('2026-02-29T00:00:00Z'),/Invalid timestamp/);
});
