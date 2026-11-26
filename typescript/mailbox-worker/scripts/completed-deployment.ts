import { setImmediate as yieldToIo } from 'node:timers/promises';
import { latestSchemaStatements, planLatestSchema, type StorageDatabase, type StorageStatement } from './latest-schema.ts';
import { ledgerSql, ledgerTableSql, verifyCompletedMigration } from './completed-migration.ts';
import { approvedPlan, liveSchemaSql, type ApprovedSchemaChange } from './approved-schema-change.ts';
import { externalActionKeysChange } from './approved-external-action-keys.ts';
import { ownerGrantsChange } from './approved-owner-grants.ts';
import { appWebhooksChange } from './approved-app-webhooks.ts';
import { appWebhookDeliveriesChange } from './approved-app-webhook-deliveries.ts';
import { ownerGrantApprovalsChange } from './approved-owner-grant-approvals.ts';
import { ownerGrantDecisionsChange } from './approved-owner-grant-decisions.ts';
import { externalActionSettlementsChange } from './approved-external-action-settlements.ts';

const migrationGateSql="SELECT 1 found FROM sqlite_master WHERE type='trigger' AND name='data_migration_write_gate'";
const metadataSql=new Set([ledgerTableSql,ledgerSql,migrationGateSql,...[externalActionKeysChange,ownerGrantsChange,appWebhooksChange,appWebhookDeliveriesChange,ownerGrantApprovalsChange,ownerGrantDecisionsChange,externalActionSettlementsChange].map(change=>change.liveTriggerSql),
    "SELECT name FROM sqlite_master WHERE type='table'",liveSchemaSql]);

// Do not pass the underlying database or statements to schema inspection. Even
// a future planner change cannot dispatch a write through this capability.
function metadataDatabase(db:StorageDatabase):StorageDatabase {
    const wrap=(statement:StorageStatement):StorageStatement=>({
        bind:(...values)=>wrap(statement.bind(...values)),
        all:<T>()=>statement.all<T>(),first:<T>(column?:string)=>statement.first<T>(column)
    });
    return {
        prepare(sql){
            if(typeof sql!=='string'||(!metadataSql.has(sql)&&!/^PRAGMA (?:table_info|foreign_key_list)\("[a-z_][a-z0-9_]*"\)$/.test(sql)))
                throw Error('Restricted deployment rejected SQL before database dispatch.');
            return wrap(db.prepare(sql));
        },
        async batch(){throw Error('Restricted deployment forbids schema batches.');}
    };
}

// With the owner's per-run opt-ins (approved), the only exceptions to the empty schema plan are those approved changes'
// tables and indexes (approved-schema-change.ts). Nothing else may be pending.
export async function prepareCompletedDeployment(db:StorageDatabase,planSchema=planLatestSchema,approved:readonly ApprovedSchemaChange[]=[]) {
    const names=approved.map(change=>change.table).join(' and ');
    const metadata=metadataDatabase(db);
    async function verify(stage:string,allowApproved=false) {
        const conversion=await verifyCompletedMigration(metadata);
        if(await metadata.prepare(migrationGateSql).first())
            throw Error('Restricted deployment requires the migration write gate to be absent; no trigger changes are permitted.');
        const schemaPlan=await planSchema(metadata);
        let pending:string[]=[];
        if(schemaPlan.statements.length){
            if(!approved.length)throw Error('Restricted deployment requires an empty schema plan; no schema changes are permitted.');
            if(!allowApproved)throw Error('Restricted deployment requires an empty schema plan after the approved '+names+' statements; release stopped.');
            pending=approvedPlan(schemaPlan.statements,approved);
        }
        for(const change of approved)await change.assertLive(metadata,!pending.some(sql=>change.owns(sql)));
        console.log('Completed-only validation: '+JSON.stringify({stage,...conversion,schemaStatements:pending.length}));
        return {conversion,schemaPlan,pending};
    }
    const preflight=(await verify('preflight',true)).pending;
    const ddl=(await latestSchemaStatements()).filter(sql=>/^CREATE TABLE `data_migrations` /.test(sql)).map(sql=>sql.trim());
    if(ddl.length!==1||!/^CREATE TABLE `data_migrations` \([^;]+\);$/.test(ddl[0]))
        throw Error('Restricted deployment requires exactly one generated migration ledger statement.');
    const ledgerCheck=ddl[0].replace('CREATE TABLE','CREATE TABLE IF NOT EXISTS');
    return {
        async confirm(){
            const before=(await verify('before-ledger-check',true)).pending;
            if(JSON.stringify(before)!==JSON.stringify(preflight))
                throw Error('The approved '+names+' statements changed after preflight; no schema statements applied.');
            // Without the opt-in this is the only write-capable operation in completed-only
            // mode; with it, only the approved batch below follows. Neither calls the
            // historical converter or the generic schema applier.
            await yieldToIo();
            const result=await db.batch([db.prepare(ledgerCheck)]);
            const item=Array.isArray(result)&&result.length===1?result[0]:null;
            if(item?.success!==true||item.meta?.rows_written!==0||item.meta?.changes!==0||item.meta?.changed_db!==false)
                throw Error('Ledger IF NOT EXISTS did not confirm zero changes; release stopped before activation.');
            console.log('Completed-only ledger check: '+JSON.stringify({statements:1,rows_written:0,changes:0,changed_db:false}));
            if(preflight.length){
                // The approved statements' own text, as one D1 transaction; the previous version keeps serving because
                // it never reads the new table. A failed or unconfirmed batch stops before any upload.
                await yieldToIo();
                const applied=await db.batch(preflight.map(sql=>db.prepare(sql)));
                if(!Array.isArray(applied)||applied.length!==preflight.length||applied.some(item=>item?.success!==true))
                    throw Error('Approved '+names+' schema batch not confirmed; release stopped before any upload.');
                console.log('Completed-only approved schema: '+JSON.stringify({statements:preflight.length,tables:approved.filter(change=>preflight.some(sql=>change.owns(sql))).map(change=>change.table)}));
            }
            const {conversion,schemaPlan}=await verify('after-ledger-check');
            return {conversion,schemaPlan,approvedSchemaApplied:approved.length?preflight:undefined};
        },
        verify:()=>verify('before-active')
    };
}
