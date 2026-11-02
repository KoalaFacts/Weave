import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { test } from 'node:test';
import { auditExistingD1 } from '../scripts/audit-existing-d1.ts';
import type { StorageDatabase } from '../scripts/latest-schema.ts';

function fixture() {
    const sql = new DatabaseSync(':memory:');
    const statements:string[] = [];
    sql.exec([
        'CREATE TABLE humans(human_id TEXT PRIMARY KEY)',
        'CREATE TABLE accounts(account_id TEXT PRIMARY KEY,human_id TEXT,display_name TEXT,description TEXT,created_at INTEGER,updated_at INTEGER)',
        'CREATE TABLE account_addresses(address_id TEXT,account_id TEXT)',
        'CREATE TABLE contact_requests(requester_account_id TEXT,recipient_account_id TEXT)',
        'CREATE TABLE contacts(owner_account_id TEXT,to_account_id TEXT)',
        'CREATE TABLE mailbox_records(seq TEXT PRIMARY KEY,sender_account_id TEXT,recipient_account_id TEXT)',
        'CREATE TABLE message_delivery(message_seq TEXT,state TEXT)',
        'CREATE TABLE client_grants(account_id TEXT,human_id TEXT)',
        'CREATE TABLE agent_keys(kid TEXT)',
        'CREATE TABLE mcp_idempotency(idempotency_key TEXT)',
        'CREATE TABLE data_migrations(script_id TEXT,checksum TEXT,completed_at INTEGER)',
        'CREATE TABLE oauth_identity_links(account_id TEXT)',
    ].join(';') + ';');
    sql.exec("INSERT INTO data_migrations(script_id,checksum,completed_at) VALUES('normalize-mailbox-records','ecd612f0528cc03d0dee877269a5546e3531cd4d3533325fc3441938407efe62',17919826570270000)");
    const db:StorageDatabase={
        prepare(statement:string) {
            statements.push(statement);
            if (!/^\s*(SELECT|PRAGMA table_info\s*\()/i.test(statement))
                throw Error('Audit attempted a non-read-only statement');
            const prepared = sql.prepare(statement);
            return {
                bind(...args:(string|number|null|ArrayBuffer)[]) {
                    prepared.bind(...args as Parameters<typeof prepared.bind>);
                    return this;
                },
                async all<T>() {
                    return {results:prepared.all() as T[]};
                },
                async first<T>() {
                    return (prepared.get() ?? null) as T|null;
                },
            };
        },
        async batch() { throw Error('Read-only D1 audit must not batch statements'); },
    };
    return {sql,db,statements};
}

test('current six-column accounts, messages, ACK and identity links are counted without reading contents', async()=>{
    const {sql,db,statements}=fixture();
    try {
        sql.exec("INSERT INTO humans VALUES('human-A'); INSERT INTO accounts(account_id,human_id) VALUES('a','human-A'),('b',NULL);"
            + "INSERT INTO mailbox_records VALUES('m1','a','b'); INSERT INTO message_delivery VALUES('m1','acknowledged'); INSERT INTO oauth_identity_links VALUES('a');");
        const report=await auditExistingD1(db);
        assert.equal(report.status,'observed');
        assert.equal(report.schema,'six-column-human-linked');
        assert.equal(report.rowCounts.accounts,'2');
        assert.equal(report.rowCounts.mailbox_records,'1');
        assert.equal(report.acknowledged,'1');
        assert.equal(report.humanLinkedAccounts,'1');
        assert.equal(report.orphanCounts['delivery-message'],'0');
        assert.equal(report.rowCounts.oauth_identity_links,'1');
        assert.equal(report.orphanCounts['legacy-oauth-links-account'],'0');
        assert.equal(report.historicalPreservationProven,false);
        assert.equal(report.migrationPermitted,false);
        assert.deepEqual(report.normalizeMailboxLedger,{state:'complete',completedAtTicks:'17919826570270000'});
        assert.equal(JSON.stringify(report).includes('human-A'),false);
        assert.equal(JSON.stringify(report).includes('m1'),false);
        assert.ok(statements.every(statement=>/^\s*(SELECT|PRAGMA table_info\s*\()/i.test(statement)));
    } finally {sql.close();}
});

test('missing referenced account is a blocking finding, never silently treated as preserved',async()=>{
    const {sql,db}=fixture();
    try {
        sql.exec("INSERT INTO accounts(account_id) VALUES('a');"
            + "INSERT INTO mailbox_records VALUES('m1','a','missing');"
            + "INSERT INTO message_delivery VALUES('m1','pending');");
        const report=await auditExistingD1(db);
        assert.equal(report.status,'needs-review');
        assert.equal(report.orphanCounts['messages-recipient'],'1');
        assert.ok(report.findings.includes('orphaned-reference-messages-recipient'));
        assert.equal(report.rowCounts.mailbox_records,'1');
    } finally {sql.close();}
});

test('the old eight-column account schema is observed but never authorized to migrate',async()=>{
    const {sql,db}=fixture();
    try {
        sql.exec('DROP TABLE accounts; CREATE TABLE accounts('
            +'account_id TEXT,human_id TEXT,display_name TEXT,description TEXT,created_at INTEGER,updated_at INTEGER,legacy_control TEXT,legacy_nonce TEXT);');
        const report=await auditExistingD1(db);
        assert.equal(report.schema,'other-review-required');
        assert.equal(report.status,'needs-review');
        assert.ok(report.findings.includes('account-schema-requires-review'));
        assert.equal(report.accountColumns.length,8);
        assert.equal(report.migrationPermitted,false);
    } finally {sql.close();}
});

test('a missing core table fails closed and still records existing table counts',async()=>{
    const {sql,db}=fixture();
    try {
        sql.exec('DROP TABLE contacts;');
        const report=await auditExistingD1(db);
        assert.equal(report.status,'needs-review');
        assert.ok(report.missingRequiredTables.includes('contacts'));
        assert.equal(report.rowCounts.contacts,null);
        assert.ok(report.findings.includes('missing-core-tables'));
    } finally {sql.close();}
});

test('missing old OAuth identity link table remains an explicit history-verification blocker',async()=>{
    const {sql,db}=fixture();
    try {
        sql.exec('DROP TABLE oauth_identity_links;');
        const report=await auditExistingD1(db);
        assert.equal(report.status,'needs-review');
        assert.equal(report.rowCounts.oauth_identity_links,null);
        assert.ok(report.findings.includes('legacy-oauth-identity-history-unverified'));
        assert.equal(report.orphanCounts['legacy-oauth-links-account'],null);
    } finally {sql.close();}
});

test('changed migration completion time is observable without reading private data',async()=>{
    const {sql,db}=fixture();
    try {
        sql.exec('UPDATE data_migrations SET completed_at=17935778419690000');
        const report=await auditExistingD1(db);
        assert.deepEqual(report.normalizeMailboxLedger,{state:'complete',completedAtTicks:'17935778419690000'});
        assert.equal(report.status,'observed');
    } finally {sql.close();}
});

test('mismatched migration checksum is a blocking provenance finding',async()=>{
    const {sql,db}=fixture();
    try {
        sql.exec("UPDATE data_migrations SET checksum='not-the-expected-checksum'");
        const report=await auditExistingD1(db);
        assert.deepEqual(report.normalizeMailboxLedger,{state:'unexpected-checksum',completedAtTicks:'17919826570270000'});
        assert.equal(report.status,'needs-review');
        assert.ok(report.findings.includes('normalize-mailbox-ledger-unexpected-checksum'));
        assert.equal(JSON.stringify(report).includes('not-the-expected-checksum'),false);
    } finally {sql.close();}
});

test('missing migration ledger is reported without attempting repair',async()=>{
    const {sql,db}=fixture();
    try {
        sql.exec('DROP TABLE data_migrations');
        const report=await auditExistingD1(db);
        assert.deepEqual(report.normalizeMailboxLedger,{state:'unavailable',completedAtTicks:null});
        assert.equal(report.status,'needs-review');
        assert.ok(report.findings.includes('normalize-mailbox-ledger-unavailable'));
    } finally {sql.close();}
});
