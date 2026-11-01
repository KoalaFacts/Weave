import assert from 'node:assert/strict';
import test from 'node:test';
import { latestSchemaStatements, planLatestSchema } from '../scripts/latest-schema.ts';
import { applyHumanAccountUpgrade, humanAccountMigrationId, planHumanAccountUpgrade } from '../scripts/human-account-upgrade.ts';
import { start } from './harness.ts';
import { createOwnedAgent, oauthAccountActive, signInHuman } from '../src/humans.ts';
import type { Env } from '../src/database.ts';

// Exact eight-column pre-human production accounts table. Dropping/rebuilding
// this parent table would violate live contacts/messages/ACK foreign keys.
const legacyAccounts = `CREATE TABLE "accounts" (
    "account_id" text PRIMARY KEY NOT NULL,
    "control_hash" text NOT NULL,
    "control_hash_scheme" text NOT NULL,
    "control_hash_parameters" text NOT NULL,
    "display_name" text,
    "description" text,
    "created_at" integer,
    "updated_at" integer,
    CONSTRAINT "accounts_control_digest" CHECK(length(control_hash)=64 AND control_hash NOT GLOB '*[^0-9a-f]*'),
    CONSTRAINT "accounts_control_scheme" CHECK(control_hash_scheme='sha256-utf8-v1'),
    CONSTRAINT "accounts_control_parameters" CHECK(json_valid(control_hash_parameters))
)`;
const params = JSON.stringify({algorithm:'SHA-256',inputEncoding:'utf-8',digestEncoding:'hex-lower',normalization:'none',salt:null,rounds:1});
const ids = {
    a:'11111111-2222-4333-8444-555555555555',
    b:'22222222-3333-4444-8555-666666666666'
};
async function legacyFixture() {
    const h=await start({migrate:false});
    try {
        const ddl=await latestSchemaStatements();
        // Build all *other* current tables and indexes on actual workerd D1,
        // preserving exactly the old accounts layout without foreign_keys=OFF.
        const skip=(sql:string)=>['accounts','humans','accounts_human','humans_subject'].includes(
            /^CREATE (?:TABLE|(?:UNIQUE )?INDEX)\s+["`]?([a-z_]+)["`]?/i.exec(sql)?.[1]??'');
        await h.db.prepare(legacyAccounts).run();
        for(const statement of ddl.filter(sql=>!skip(sql)))
            await h.db.prepare(statement).run();
        for(const [id,digest] of [[ids.a,'a'.repeat(64)],[ids.b,'b'.repeat(64)]])
            await h.db.prepare('INSERT INTO accounts(account_id,control_hash,control_hash_scheme,control_hash_parameters,display_name,created_at,updated_at) VALUES(?,?,?,?,?,?,?)')
                .bind(id,digest,'sha256-utf8-v1',params,'Existing',123456789,123456789).run();
        await h.db.prepare("INSERT INTO account_addresses(address_id,account_id,seq,is_public,created_at,legacy_relay_endpoint) VALUES('address-a',?,1,1,1,'https://example.invalid/relay')")
            .bind(ids.a).run();
        await h.db.prepare('INSERT INTO contacts(owner_account_id,to_account_id,connection_token) VALUES(?,?,?)')
            .bind(ids.a,ids.b,'c'.repeat(64)).run();
        await h.db.prepare("INSERT INTO mailbox_records(seq,message_id,sender_account_id,recipient_account_id,payload,payload_size,created_at,expires_at) VALUES(7,'msg-1',?,?,x'6869',2,1,100000)")
            .bind(ids.a,ids.b).run();
        await h.db.prepare("INSERT INTO message_delivery(message_seq,state,terminal_at,protocol_version,purpose,fingerprint,fingerprint_scheme) VALUES(7,'acknowledged',200,1,0,'old-fingerprint','legacy-envelope-v1')")
            .run();
        await h.db.prepare("INSERT INTO oauth_identity_links(issuer,resource,subject_digest,account_id,created_at,updated_at) VALUES('https://issuer.invalid','https://demo.invalid/mcp',?,?,1,1)")
            .bind('d'.repeat(64),ids.a).run();
        await h.db.prepare("INSERT INTO mailbox_revision(singleton,token,created_at,updated_at) VALUES(1,'initial',1,1)").run();
        await h.db.prepare("INSERT INTO counters(name,value,created_at,updated_at) VALUES('record-seq',8,1,1)").run();
        await h.db.prepare('CREATE TABLE d1_migrations(id INTEGER PRIMARY KEY,name TEXT)').run();
        await h.db.prepare("INSERT INTO d1_migrations(id,name) VALUES(8,'historic-entry')").run();
        return h;
    } catch(error){await h.close();throw error;}
}
const inspect=async(db: ReturnType<typeof start> extends Promise<infer T>?T['db']:never)=>{
    const query=async(sql:string)=>(await db.prepare(sql).all()).results;
    return {
        accounts: await query('SELECT * FROM accounts ORDER BY account_id'),
        addresses: await query('SELECT * FROM account_addresses'),
        contacts: await query('SELECT * FROM contacts'),
        messages: await query('SELECT seq,message_id,sender_account_id,recipient_account_id,hex(payload) AS payload,payload_size FROM mailbox_records'),
        acks: await query('SELECT * FROM message_delivery'),
        links: await query('SELECT * FROM oauth_identity_links'),
        history: await query('SELECT * FROM d1_migrations'),
    };
};

test('human D1 migration preserves legacy accounts, contacts, message bytes, ACKs and OAuth links without rebuilding accounts',async()=>{
    const h=await legacyFixture();
    try {
        const before=await inspect(h.db);
        const oldSql=await h.db.prepare("SELECT sql FROM sqlite_master WHERE name='accounts'").first('sql');
        const plan=await planHumanAccountUpgrade(h.db);
        assert.equal(plan.status,'legacy');
        assert.equal(plan.statements.length,4);
        assert.ok(plan.statements.every(sql=>!/\b(?:DROP|DELETE|UPDATE|RENAME)\b/i.test(sql)));
        assert.deepEqual(await inspect(h.db),before,'read-only planning cannot mutate old D1');
        await applyHumanAccountUpgrade(h.db);
        const after=await inspect(h.db);
        assert.deepEqual(after.accounts.map(({human_id,...rest})=>rest),before.accounts);
        assert.deepEqual({...after,accounts:before.accounts},before);
        assert.equal(await h.db.prepare('PRAGMA foreign_key_check').all().then(x=>x.results.length),0);
        assert.equal(await h.db.prepare('SELECT checksum FROM data_migrations WHERE script_id=?')
            .bind(humanAccountMigrationId).first('checksum'),plan.checksum);
        assert.equal((await planLatestSchema(h.db)).statements.length,0,'generated target schema must match additive migration exactly');
        const newSql=await h.db.prepare("SELECT sql FROM sqlite_master WHERE name='accounts'").first('sql');
        assert.ok(newSql.includes(oldSql.slice(0,35)),'the existing parent table was ALTERed, not replaced');
        assert.equal((await planHumanAccountUpgrade(h.db)).status,'current');
        assert.equal((await applyHumanAccountUpgrade(h.db)).status,'current','repeat is read-only');
        assert.deepEqual((await inspect(h.db)).accounts,after.accounts);
        // New human-owned agents are operational without creating a control
        // credential or inheriting the old account's private authority.
        const env={db:h.db,REGISTRATION_ENABLED:'true',REGISTRATION_MAX_ACCOUNTS:'100',
            REGISTRATION_PER_IP_PER_HOUR:'100',REGISTRATION_GLOBAL_PER_HOUR:'100'} as unknown as Env;
        const signed=await signInHuman(env,{provider:'github',subject:'456',providerCreatedAt:'2020-01-01T00:00:00Z',displayLogin:'alice'});
        assert.equal(signed.ok,true);
        if(!signed.ok)throw Error('Expected a human');
        const owned=await createOwnedAgent(env,{humanId:signed.humanId,displayName:'Human Agent',sourceIp:'203.0.113.99'});
        assert.equal(owned.ok,true);
        if(!owned.ok)throw Error('Expected a new Agent');
        assert.equal(await oauthAccountActive(env,owned.accountId),false,'external bearer cannot claim owned agent');
        assert.equal(await oauthAccountActive(env,owned.accountId,signed.humanId),true);
        const row=await h.db.prepare('SELECT control_hash,control_hash_scheme,control_hash_parameters FROM accounts WHERE account_id=?')
            .bind(owned.accountId).first<{control_hash:string;control_hash_scheme:string;control_hash_parameters:string}>();
        assert.match(row!.control_hash,/^[0-9a-f]{64}$/);
        assert.equal(row!.control_hash_parameters,'{}');
        assert.equal(row!.control_hash_scheme,'sha256-utf8-v1');
    } finally {await h.close();}
});

test('human D1 migration rejects unreviewed legacy column drift without any DDL or data loss',async()=>{
    const h=await legacyFixture();
    try {
        await h.db.prepare('ALTER TABLE accounts ADD COLUMN unexpected TEXT').run();
        const before=await inspect(h.db);
        await assert.rejects(planHumanAccountUpgrade(h.db),/exact pre-human accounts columns/);
        await assert.rejects(applyHumanAccountUpgrade(h.db),/exact pre-human accounts columns/);
        assert.deepEqual(await inspect(h.db),before);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM sqlite_master WHERE name='humans'").first('n'),0);
        assert.equal(await h.db.prepare('SELECT checksum FROM data_migrations WHERE script_id=?')
            .bind(humanAccountMigrationId).first('checksum'),null);
    }finally{await h.close();}
});
