import { generateSQLiteDrizzleJson, generateSQLiteMigration } from 'drizzle-kit/api';
import { getTableConfig, SQLiteSyncDialect } from 'drizzle-orm/sqlite-core';
import { is, SQL } from 'drizzle-orm';
import { DatabaseSync } from 'node:sqlite';
import { setImmediate as yieldToIo } from 'node:timers/promises';
import * as schema from '../src/schema.ts';
import { assertAdditiveSchemaPlan } from './schema-plan.ts';
export interface StorageStatement {
    bind(...values: (string|number|null|ArrayBuffer)[]): StorageStatement;
    all<T>(): Promise<{results:T[]}>;
    first<T>(column?:string): Promise<T|null>;
}
export interface StorageDatabase {
    prepare(sql:string): StorageStatement;
    batch(statements:StorageStatement[]): Promise<unknown>;
}
export interface SchemaPlanOptions {omitUnapprovedOAuthIdentityLinks?:boolean;}
type SchemaObject = {type: string; name: string; tbl_name: string; sql: string};
type ColumnInfo = {name: string; type: string; notnull: number; dflt_value: unknown; pk: number};

// Generated in memory from the one maintained TypeScript schema. No SQL history.
export async function latestSchemaStatements() {
    const generated=await generateSQLiteMigration(await generateSQLiteDrizzleJson({}),await generateSQLiteDrizzleJson(schema));
    const statements=generated.filter(statement=>!/^CREATE (?:UNIQUE )?INDEX /.test(statement));
    const dialect=new SQLiteSyncDialect(),identifier=(name: string)=>'"'+name.replaceAll('"','""')+'"';
    // Kit 0.31 splits SQLite index expressions at commas. Compile the declared
    // indexes with ORM's SQL dialect so json_extract(data, path) stays one term.
    const expression=(value: SQL)=>{const query=dialect.sqlToQuery(value);if(query.params.length)throw Error('Schema expressions must not have bound values.');return query.sql;};
    for(const table of Object.values(schema)){
        const config=getTableConfig(table);
        for(const {config:index} of config.indexes)statements.push(`CREATE ${index.unique?'UNIQUE ':''}INDEX ${identifier(index.name)} ON ${identifier(config.name)} (${index.columns.map(column=>is(column,SQL)?expression(column):identifier(column.name)).join(',')})${index.where?' WHERE '+expression(index.where):''};`);
    }
    assertAdditiveSchemaPlan(statements);
    return statements;
}

const identifier=(name: string)=>'"'+name.replaceAll('"','""')+'"';
const columnShape=(column: ColumnInfo)=>({name:column.name,type:column.type.toLowerCase(),notnull:column.pk&&column.type.toLowerCase()==='integer'?1:column.notnull,default:column.dflt_value,pk:column.pk});
const canonicalSql=(sql: string)=>(sql.match(/'(?:''|[^'])*'|"(?:""|[^"])*"|`(?:``|[^`])*`|[^\s'"`;]+/g)??[]).map(token=>token.startsWith("'")?token:token.replace(/^["`]|["`]$/g,'').toLowerCase()).join('');
function checks(sql: string){
    const result=[],pattern=/\bCHECK\s*\(/ig;
    while(pattern.exec(sql)!==null){
        let depth=1,quote: string|null=null,end=pattern.lastIndex;
        for(;end<sql.length&&depth;end++){
            const c=sql[end];
            if(quote){if(c===quote){if(sql[end+1]===quote)end++;else quote=null;}continue;}
            if(c==="'"||c==='"'||c==='`')quote=c;else if(c==='(')depth++;else if(c===')')depth--;
        }
        if(depth)throw Error('Cannot inspect schema CHECK expression; explicit review required.');
        result.push(canonicalSql(sql.slice(pattern.lastIndex,end-1)));pattern.lastIndex=end;
    }
    return result.sort();
}

// Compare the live database with a fresh database generated from the sole TS
// schema. Never infer a rename or silently rebuild an existing table. Changes
// to existing columns/constraints require an explicit reviewed data operation.
export async function planLatestSchema(db: StorageDatabase,options:SchemaPlanOptions={}) {
    const ddl=await latestSchemaStatements(),expected=new DatabaseSync(':memory:');
    const existing=(await db.prepare("SELECT type,name,tbl_name,sql FROM sqlite_master WHERE type IN ('table','index')").all<SchemaObject>()).results;
    const names=new Set(existing.map(x=>x.name));
    try {
        for(const sql of ddl)expected.exec(sql);
        for(const table of Object.values(schema)){
            const name=getTableConfig(table).name;if(!names.has(name))continue;
            const want=(expected.prepare(`PRAGMA table_info(${identifier(name)})`).all() as ColumnInfo[]).map(columnShape);
            const actual=(await db.prepare(`PRAGMA table_info(${identifier(name)})`).all<ColumnInfo>()).results.map(columnShape);
            if(JSON.stringify(actual)!==JSON.stringify(want))throw Error('Existing columns differ from the latest schema in '+name+'; explicit review required before changes.');
            const foreignKeys=(rows: Record<string,unknown>[])=>rows.map(({id,...row})=>row).sort((a,b)=>JSON.stringify(a).localeCompare(JSON.stringify(b)));
            const expectedKeys=foreignKeys(expected.prepare(`PRAGMA foreign_key_list(${identifier(name)})`).all());
            const actualKeys=foreignKeys((await db.prepare(`PRAGMA foreign_key_list(${identifier(name)})`).all<Record<string,unknown>>()).results);
            if(JSON.stringify(actualKeys)!==JSON.stringify(expectedKeys))throw Error('Existing foreign keys differ from the latest schema in '+name+'; explicit review required.');
            const expectedSql=expected.prepare("SELECT sql FROM sqlite_master WHERE type='table' AND name=?").get(name)!.sql as string;
            const actualSql=existing.find(row=>row.type==='table'&&row.name===name)!.sql;
            if(JSON.stringify(checks(actualSql))!==JSON.stringify(checks(expectedSql)))throw Error('Existing CHECK constraints differ from the latest schema in '+name+'; explicit review required.');
        }
        for(const index of expected.prepare("SELECT name,sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL").all() as {name:string;sql:string}[]){
            const actual=existing.find(row=>row.type==='index'&&row.name===index.name);
            if(actual&&canonicalSql(actual.sql)!==canonicalSql(index.sql))throw Error('Existing index differs from the latest schema: '+index.name+'; explicit review required.');
        }
    } finally { expected.close(); }
    const statements=ddl.filter(sql=>{
        const match=/^CREATE (?:UNIQUE )?(?:TABLE|INDEX) [`"]([^`"]+)[`"]/i.exec(sql);
        if(!match)throw Error('Unsupported generated DDL; no schema changes applied.');
        // Demo releases may repair ordinary schema while keeping the opt-in
        // OAuth identity-link table and its indexes wholly unprovisioned until
        // the operator reviews and approves a separate D1 migration.
        if(options.omitUnapprovedOAuthIdentityLinks &&
            (match[1]==='oauth_identity_links' ||
                (/^CREATE (?:UNIQUE )?INDEX /i.test(sql) && /\bON\s+["`]oauth_identity_links["`]\s*\(/i.test(sql))))return false;
        return !names.has(match[1]);
    });
    assertAdditiveSchemaPlan(statements);
    return {statements,preservedUnmanagedTables:existing.filter(x=>x.type==='table'&&!Object.values(schema).some(table=>getTableConfig(table).name===x.name)).map(x=>x.name).sort()};
}

export async function createMissingSchema(db: StorageDatabase,options:SchemaPlanOptions={}) {
    const {statements}=await planLatestSchema(db,options);
    if(statements.length){const prepared=statements.map(sql=>db.prepare(sql));await yieldToIo();await db.batch(prepared);}
    return statements.length;
}

export async function initializeDatabase(db: StorageDatabase,now: bigint) {
    const existing=await db.prepare("SELECT 1 found FROM sqlite_master WHERE type='table' AND name='mailbox_records'").first();
    if(existing)throw Error('Initialization requires an empty database; use the independent data conversion for existing records.');
    await createMissingSchema(db);
    await db.batch([
        db.prepare("INSERT INTO mailbox_revision(singleton,token,created_at,updated_at) VALUES(1,'initial',?,?)").bind(now.toString(),now.toString()),
        db.prepare("INSERT INTO counters(name,value,created_at,updated_at) VALUES('record-seq',0,?,?)").bind(now.toString(),now.toString())
    ]);
}
