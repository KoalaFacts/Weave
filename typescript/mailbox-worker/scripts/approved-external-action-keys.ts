import { DatabaseSync } from 'node:sqlite';
import type { planLatestSchema } from './latest-schema.ts';

type SchemaPlan = Awaited<ReturnType<typeof planLatestSchema>>;

/**
 * Only the additive, *new* external-action proposal-key table and its action-id index.
 * This is a source-side guard for a future owner-approved protected release; it grants no
 * approval and cannot be activated by the ordinary completed-only deployment.
 */
export function assertExternalActionKeysSchemaPlan(plan: SchemaPlan): void {
    const [createTable, createIndex] = plan.statements;
    const reject = (): never => { throw Error('Only the approved external_action_keys schema (table and unique index) may be applied; no D1 writes attempted.'); };
    if (plan.statements.length !== 2 ||
        !/^CREATE TABLE [`"]external_action_keys[`"]\s*\(/i.test(createTable) ||
        !/^CREATE UNIQUE INDEX "external_action_keys_action" ON "external_action_keys" \("action_id"\);?$/i.test(createIndex.trim())) reject();
    const db = new DatabaseSync(':memory:');
    try {
        // Verify the actual SQLite shape, not merely a permissive SQL-name prefix.
        // The other FK target tables need not exist in SQLite for CREATE TABLE.
        db.exec(createTable);
        db.exec(createIndex);
        const cols = db.prepare('PRAGMA table_info("external_action_keys")').all() as
            {name:string;type:string;notnull:number;pk:number}[];
        const expected = [
            ['account_id','text',1,1], ['idempotency_key','text',1,2],
            ['request_hash','text',1,0], ['action_id','text',1,0], ['created_at','integer',1,0]
        ];
        if (JSON.stringify(cols.map(c=>[c.name,c.type.toLowerCase(),c.notnull,c.pk])) !== JSON.stringify(expected)) reject();
        const fks = db.prepare('PRAGMA foreign_key_list("external_action_keys")').all() as
            {table:string;from:string;to:string;on_delete:string}[];
        if (JSON.stringify(fks.map(f=>[f.from,f.table,f.to,f.on_delete]).sort()) !==
            JSON.stringify([['account_id','accounts','account_id','RESTRICT'],['action_id','external_actions','action_id','RESTRICT']].sort())) reject();
        const indexes = db.prepare('PRAGMA index_list("external_action_keys")').all() as {name:string;unique:number}[];
        if (!indexes.some(i=>i.name==='external_action_keys_action'&&i.unique===1) ||
            JSON.stringify((db.prepare('PRAGMA index_info("external_action_keys_action")').all() as {name:string}[]).map(i=>i.name)) !==
            JSON.stringify(['action_id'])) reject();
        const stored = db.prepare("SELECT sql FROM sqlite_master WHERE type='table' AND name='external_action_keys'").get() as {sql:string}|undefined;
        if (!stored || (stored.sql.match(/\bCHECK\s*\(/gi)??[]).length!==2 ||
            !stored.sql.includes('external_action_keys_key') || !stored.sql.includes('external_action_keys_hash')) reject();
    } catch { reject(); }
    finally { db.close(); }
}
