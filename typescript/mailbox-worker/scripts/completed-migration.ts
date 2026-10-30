import type { StorageDatabase } from './latest-schema.ts';

// The historical normalize-mailbox-records conversion has completed in every
// deployed database; its runner and sources were removed. Deployments only
// verify the ledger row it wrote, with the checksum it recorded.
export const completedMigration={
    scriptId:'normalize-mailbox-records',
    checksum:'ecd612f0528cc03d0dee877269a5546e3531cd4d3533325fc3441938407efe62'
} as const;

export const ledgerTableSql="SELECT sql FROM sqlite_master WHERE type='table' AND name='data_migrations'";
export const ledgerSql='SELECT script_id,checksum,CAST(completed_at AS TEXT) completed_at FROM data_migrations WHERE script_id=? LIMIT 2';

export interface CompletedMigration {alreadyCompleted:true;checksum:string;completedAt:string;}

// Read-only: never creates the ledger, converts records or retries.
export async function verifyCompletedMigration(db:StorageDatabase):Promise<CompletedMigration> {
    const table=await db.prepare(ledgerTableSql).first<{sql:string}>();
    if(!table||!/^\s*CREATE\s+TABLE\b/i.test(table.sql))throw Error('Completed migration requires an existing ordinary ledger table.');
    const rows=(await db.prepare(ledgerSql).bind(completedMigration.scriptId).all<{script_id:string;checksum:string;completed_at:string|null}>()).results;
    if(rows.length!==1||rows[0].script_id!==completedMigration.scriptId||rows[0].checksum!==completedMigration.checksum
        ||typeof rows[0].completed_at!=='string'||!/^\d+$/.test(rows[0].completed_at))
        throw Error('Completed migration with the original checksum is required; no conversion is permitted.');
    return {alreadyCompleted:true,checksum:completedMigration.checksum,completedAt:rows[0].completed_at};
}
