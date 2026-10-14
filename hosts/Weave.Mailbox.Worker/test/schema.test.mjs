import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { test } from 'node:test';
import { latestSchemaStatements } from '../scripts/latest-schema.mjs';
import { assertAdditiveSchemaPlan } from '../scripts/schema-plan.mjs';

async function database() {
    const statements=await latestSchemaStatements();
    assertAdditiveSchemaPlan(statements);
    const db=new DatabaseSync(':memory:'); db.exec('PRAGMA foreign_keys=ON');
    for(const statement of statements)db.exec(statement);
    return db;
}
function accounts(db) {
    for(const id of ['synthetic-a','synthetic-b']) db.prepare(`INSERT INTO accounts(account_id,control_hash,control_hash_scheme,control_hash_parameters,created_at,updated_at)
        VALUES(?,?,'sha256-utf8-v1',?,0,0)`).run(id,(id==='synthetic-a'?'a':'b').repeat(64),JSON.stringify({algorithm:'SHA-256',inputEncoding:'utf-8',digestEncoding:'hex-lower',normalization:'none',salt:null,rounds:1}));
}

test('latest Drizzle schema defines the approved tables without compatibility views or triggers',async()=>{
    const db=await database();
    try {
        const tables=db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").all().map(r=>r.name);
        assert.deepEqual(tables,['account_addresses','accounts','contact_cards','contact_requests','contacts','counters','data_migrations','legacy_channels','mailbox_guard','mailbox_records','mailbox_records_legacy','mailbox_revision','maintenance_state','message_delivery','rate_limits','stream_cursors','stream_leases']);
        assert.equal(db.prepare("SELECT count(*) n FROM sqlite_master WHERE type IN ('view','trigger')").get().n,0);
        const business=tables.filter(name=>name!=='data_migrations');
        assert.equal(business.reduce((n,name)=>n+db.prepare(`PRAGMA table_info(${name})`).all().length,0),128);
        assert.ok(business.flatMap(name=>db.prepare(`PRAGMA table_info(${name})`).all()).every(c=>!c.name.endsWith('_ticks')));
        assert.ok(!db.prepare('PRAGMA table_info(mailbox_records)').all().some(c=>c.name==='kind'));
    } finally { db.close(); }
});

test('message storage keeps opaque BLOB bytes, empty versus cleared bodies and exact int64 values',async()=>{
    const db=await database();
    try {
        accounts(db);
        const bytes=Buffer.alloc(65536);for(let i=0;i<bytes.length;i++)bytes[i]=i%256;
        const insert=db.prepare(`INSERT INTO mailbox_records(seq,message_id,sender_account_id,recipient_account_id,payload,payload_size,payload_encoding,created_at,expires_at,updated_at)
            VALUES(?,?,'synthetic-a','synthetic-b',?,?,'application/octet-stream',?,?,?)`);
        const exact=17919720000000001n;
        insert.run(9007199254740993n,'message-boundary',bytes,bytes.length,exact,exact+10000000n,exact);
        const row=db.prepare('SELECT CAST(seq AS TEXT) seq,CAST(created_at AS TEXT) created_at,typeof(payload) type,payload FROM mailbox_records').get();
        assert.equal(row.seq,'9007199254740993');assert.equal(row.created_at,exact.toString());assert.equal(row.type,'blob');assert.deepEqual(Buffer.from(row.payload),bytes);
        assert.throws(()=>insert.run(9007199254740994n,'oversize',Buffer.alloc(65537),65537,exact,exact+10000000n,exact),/CHECK constraint/);
        insert.run(9007199254740994n,'empty',Buffer.alloc(0),0,exact,exact+10000000n,exact);
        insert.run(9007199254740995n,'cleared',null,0,exact,exact+10000000n,exact);
        assert.deepEqual(db.prepare("SELECT message_id,typeof(payload) type FROM mailbox_records WHERE message_id IN ('empty','cleared') ORDER BY message_id").all().map(r=>({...r})),[{message_id:'cleared',type:'null'},{message_id:'empty',type:'blob'}]);
    } finally { db.close(); }
});

test('schema plans reject destructive changes before any application',()=>{
    for(const statement of ['DROP TABLE accounts','ALTER TABLE accounts DROP COLUMN control_hash','PRAGMA foreign_keys=OFF','DELETE FROM d1_migrations','CREATE TABLE replacement(x); DROP TABLE accounts'])
        assert.throws(()=>assertAdditiveSchemaPlan([statement]),/review|destructive|unsupported/);
    assert.doesNotThrow(()=>assertAdditiveSchemaPlan(['CREATE TABLE example(id TEXT PRIMARY KEY)','ALTER TABLE example ADD COLUMN created_at INTEGER','CREATE INDEX example_created ON example(created_at)']));
    assert.doesNotThrow(()=>assertAdditiveSchemaPlan(['CREATE TABLE child(owner TEXT REFERENCES parent(id) ON DELETE RESTRICT)']));
});

test('live schema planning preserves unmanaged history and stops on column drift before any DDL',async()=>{
    const {planLatestSchema,createMissingSchema}=await import('../scripts/latest-schema.mjs');
    const {start}=await import('./harness.mjs');const h=await start();
    try{
        await h.db.prepare('CREATE TABLE d1_migrations(id INTEGER PRIMARY KEY,name TEXT)').run();
        await h.db.prepare("INSERT INTO d1_migrations VALUES(1,'original')").run();
        const plan=await planLatestSchema(h.db);assert.equal(plan.statements.length,0);assert.ok(plan.preservedUnmanagedTables.includes('d1_migrations'));
        await h.db.prepare('ALTER TABLE accounts ADD COLUMN retained_extra TEXT').run();
        await assert.rejects(createMissingSchema(h.db),/Existing columns differ.*accounts/);
        assert.equal(await h.db.prepare('SELECT name FROM d1_migrations').first('name'),'original');
        assert.ok((await h.db.prepare('PRAGMA table_info(accounts)').all()).results.some(c=>c.name==='retained_extra'));
    }finally{await h.close();}
});
