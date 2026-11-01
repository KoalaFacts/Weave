import { createHash } from 'node:crypto';
import { latestSchemaStatements, type StorageDatabase } from './latest-schema.ts';

// This migration is intentionally additive. D1 enforces foreign keys during
// batch transactions, so rebuilding/dropping referenced accounts is unsafe:
// existing contacts, messages, receipts and OAuth links must not be touched.
export const humanAccountMigrationId = 'human-onboarding-accounts-additive-v1';
const oldColumns = [
    ['account_id','text',1,1], ['control_hash','text',1,0],
    ['control_hash_scheme','text',1,0], ['control_hash_parameters','text',1,0],
    ['display_name','text',0,0], ['description','text',0,0],
    ['created_at','integer',0,0], ['updated_at','integer',0,0]
] as const;
const originalChecks = ['accounts_control_digest','accounts_control_parameters','accounts_control_scheme'];

export interface HumanAccountUpgradePlan {
    status: 'current' | 'legacy';
    statements: string[];
    checksum: string | null;
}

export async function planHumanAccountUpgrade(db: StorageDatabase): Promise<HumanAccountUpgradePlan> {
    const columns = (await db.prepare('PRAGMA table_info("accounts")').all<{
        name:string;type:string;notnull:number;pk:number;dflt_value:unknown
    }>()).results;
    if (columns.length === 9 && columns[8].name === 'human_id') {
        // The normal schema planner will verify every column, CHECK, foreign
        // key and index before an approved already-upgraded release.
        return {status:'current',statements:[],checksum:null};
    }
    if (columns.length !== oldColumns.length || columns.some((c,i) =>
        c.name !== oldColumns[i][0] || c.type.toLowerCase() !== oldColumns[i][1] ||
        c.notnull !== oldColumns[i][2] || c.pk !== oldColumns[i][3] || c.dflt_value !== null))
        throw Error('Human onboarding migration requires the exact pre-human accounts columns; no D1 write attempted.');

    const sql = await db.prepare("SELECT sql FROM sqlite_master WHERE type='table' AND name='accounts'").first<string>('sql');
    const checks = [...(sql ?? '').matchAll(/\bCONSTRAINT\s+["\x60]?([A-Za-z0-9_]+)["\x60]?/gi)]
        .map(match => match[1]).sort();
    if (JSON.stringify(checks) !== JSON.stringify(originalChecks) ||
        (await db.prepare('PRAGMA foreign_key_list("accounts")').all()).results.length)
        throw Error('Human onboarding migration requires the exact pre-human accounts constraints and foreign keys.');

    const partial = await db.prepare("SELECT name FROM sqlite_master WHERE name IN ('humans','humans_subject','accounts_human')").all<{name:string}>();
    if (partial.results.length)
        throw Error('Human onboarding migration detected a partially upgraded schema; no D1 write attempted.');
    const ledger = await db.prepare('SELECT checksum FROM data_migrations WHERE script_id=?')
        .bind(humanAccountMigrationId).first<string>('checksum');
    if (ledger !== null)
        throw Error('Human onboarding ledger exists without the approved schema; no D1 write attempted.');
    const broken = await db.prepare('PRAGMA foreign_key_check').all();
    if (broken.results.length)
        throw Error('Human onboarding cannot proceed while old D1 foreign keys are invalid.');

    const ddl = await latestSchemaStatements();
    function required(pattern: RegExp): string {
        const statements=ddl.filter(statement=>pattern.test(statement));
        if(statements.length!==1)throw Error('Generated human schema DDL is ambiguous; no D1 write attempted.');
        return statements[0];
    }
    const statements = [
        required(/^CREATE TABLE ["\x60]humans["\x60]\s*\(/i),
        required(/^CREATE UNIQUE INDEX ["\x60]humans_subject["\x60]\s+ON/i),
        // SQLite/D1 supports adding nullable REFERENCES columns to populated
        // tables without rewriting rows or dropping referenced parent tables.
        // Keep original NOT NULL credentials; human-owned agents have random
        // unclaimable markers and invalid '{}' authentication parameters.
        `ALTER TABLE "accounts" ADD COLUMN "human_id" text REFERENCES "humans"("human_id") ON DELETE restrict CONSTRAINT "accounts_owned_control_unclaimable" CHECK (human_id IS NULL OR control_hash_parameters='{}');`,
        required(/^CREATE INDEX ["\x60]accounts_human["\x60]\s+ON/i)
    ];
    const checksum=createHash('sha256').update(statements.join('\n')).digest('hex');
    return {status:'legacy',statements,checksum};
}

export async function applyHumanAccountUpgrade(db: StorageDatabase): Promise<HumanAccountUpgradePlan> {
    const plan=await planHumanAccountUpgrade(db);
    if(plan.status==='current')return plan;
    const now=(BigInt(Date.now())*10000n).toString();
    const sql=`INSERT INTO data_migrations(script_id,checksum,created_at,updated_at,completed_at) VALUES(?,?,?,?,?)`;
    // D1 batch is atomic; either all 4 additive DDL statements and the
    // immutable ledger entry apply, or none do. No DROP/DELETE/UPDATE.
    await db.batch([...plan.statements.map(statement=>db.prepare(statement)),
        db.prepare(sql).bind(humanAccountMigrationId,plan.checksum!,now,now,now)]);
    const verified=await planHumanAccountUpgrade(db);
    if(verified.status!=='current'||(await db.prepare('PRAGMA foreign_key_check').all()).results.length)
        throw Error('Human onboarding post-migration validation failed; retain maintenance.');
    return plan;
}
