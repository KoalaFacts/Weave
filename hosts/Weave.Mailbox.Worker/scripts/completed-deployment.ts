import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { setImmediate as yieldToIo } from 'node:timers/promises';
import { latestSchemaStatements, planLatestSchema, type StorageDatabase, type StorageStatement } from './latest-schema.ts';

const ledgerTableSql="SELECT sql FROM sqlite_master WHERE type='table' AND name='data_migrations'";
const ledgerSql='SELECT script_id,checksum,CAST(completed_at AS TEXT) completed_at FROM data_migrations WHERE script_id=? LIMIT 2';
const scriptId='normalize-mailbox-records';
const metadataSql=new Set([ledgerTableSql,ledgerSql,
    "SELECT name FROM sqlite_master WHERE type='table'",
    "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE type IN ('table','index')"]);

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

export async function prepareCompletedDeployment(db:StorageDatabase,root:string,planSchema=planLatestSchema) {
    const metadata=metadataDatabase(db);
    const inputs=['scripts/data-migrations/normalize-records.ts','scripts/data-migrations/legacy-records.ts','src/time.ts','src/credentials.ts'];
    const checksum=createHash('sha256').update((await Promise.all(inputs.map(path=>readFile(join(root,path),'utf8')))).join('\n')).digest('hex');
    async function verify(stage:string) {
        const table=await metadata.prepare(ledgerTableSql).first<{sql:string}>();
        if(!table||!/^\s*CREATE\s+TABLE\b/i.test(table.sql))throw Error('Completed migration requires an existing ordinary ledger table.');
        const rows=(await metadata.prepare(ledgerSql).bind(scriptId).all<{script_id:string;checksum:string;completed_at:string|null}>()).results;
        if(rows.length!==1||rows[0].script_id!==scriptId||rows[0].checksum!==checksum||typeof rows[0].completed_at!=='string'||!/^\d+$/.test(rows[0].completed_at))
            throw Error('Completed migration with the original checksum is required; no conversion is permitted.');
        const schemaPlan=await planSchema(metadata);
        if(schemaPlan.statements.length)throw Error('Restricted deployment requires an empty schema plan; no schema changes are permitted.');
        const conversion={alreadyCompleted:true,checksum,completedAt:rows[0].completed_at};
        console.log('Completed-only validation: '+JSON.stringify({stage,...conversion,schemaStatements:0}));
        return {conversion,schemaPlan};
    }
    await verify('before-maintenance');
    const ddl=(await latestSchemaStatements()).filter(sql=>/^CREATE TABLE `data_migrations` /.test(sql)).map(sql=>sql.trim());
    if(ddl.length!==1||!/^CREATE TABLE `data_migrations` \([^;]+\);$/.test(ddl[0]))
        throw Error('Restricted deployment requires exactly one generated migration ledger statement.');
    const ledgerCheck=ddl[0].replace('CREATE TABLE','CREATE TABLE IF NOT EXISTS');
    return {
        async confirm(){
            await verify('maintenance');
            // This is the only write-capable operation in completed-only mode.
            // It never calls the historical converter or the schema applier.
            await yieldToIo();
            const result=await db.batch([db.prepare(ledgerCheck)]);
            const item=Array.isArray(result)&&result.length===1?result[0]:null;
            if(item?.success!==true||item.meta?.rows_written!==0||item.meta?.changes!==0||item.meta?.changed_db!==false)
                throw Error('Ledger IF NOT EXISTS did not confirm zero changes; maintenance retained.');
            console.log('Completed-only ledger check: '+JSON.stringify({statements:1,rows_written:0,changes:0,changed_db:false}));
            return verify('after-ledger-check');
        },
        verify:()=>verify('before-active')
    };
}
