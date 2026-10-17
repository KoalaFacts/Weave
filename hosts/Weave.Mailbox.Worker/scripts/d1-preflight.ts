import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const scriptId='normalize-mailbox-records';
const schemaSql="SELECT type,name,tbl_name,sql FROM sqlite_master WHERE type IN ('table','index') ORDER BY type,name LIMIT 513";
const ledgerSql="SELECT script_id,checksum,CAST(completed_at AS TEXT) AS completed_at FROM data_migrations WHERE script_id = ? LIMIT 2";
const hash=(text:string)=>createHash('sha256').update(text).digest('hex');
const uuid=(value:unknown):value is string=>typeof value==='string'&&/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(value)&&value!=='00000000-0000-0000-0000-000000000000';
function record(value:unknown):Record<string,unknown> {
    return value!==null&&typeof value==='object'&&!Array.isArray(value)?value as Record<string,unknown>:{};
}
async function jsonBody(response:Response):Promise<Record<string,unknown>> {
    if(!response.body)throw Error('API response has no body.');
    const reader=response.body.getReader(),chunks:Uint8Array[]=[];let size=0;
    try {
        for(;;){
            const {done,value}=await reader.read();if(done)break;
            size+=value.length;if(size>262144)throw Error('API response is too large.');chunks.push(value);
        }
    } finally {await reader.cancel().catch(()=>{});reader.releaseLock();}
    try {return record(JSON.parse(Buffer.concat(chunks).toString('utf8')));}catch {throw Error('API response is not valid JSON.');}
}

// This entry never imports deployment, migration, schema generation or D1 clients
// with write-capable initialization. The only SQL sent is the two constants above.
export async function inspectD1({env=process.env,fetchImpl=fetch}: {env?:NodeJS.ProcessEnv;fetchImpl?:typeof fetch}={}) {
    const account=env.CLOUDFLARE_ACCOUNT_ID,token=env.CLOUDFLARE_API_TOKEN;
    if(!/^[a-f0-9]{32}$/i.test(account??''))throw Error('CLOUDFLARE_ACCOUNT_ID must identify the existing account.');
    if(!token)throw Error('CLOUDFLARE_API_TOKEN is required.');
    const root=new URL('../',import.meta.url),source=await readFile(new URL('src/schema.ts',root),'utf8');
    const inputs=['scripts/data-migrations/normalize-records.ts','scripts/data-migrations/legacy-records.ts','src/time.ts','src/credentials.ts'];
    const expectedChecksum=hash((await Promise.all(inputs.map(path=>readFile(new URL(path,root),'utf8')))).join('\n'));
    const apiRoot='https://api.cloudflare.com/client/v4/accounts/'+account;
    async function request(path:string,label:string,query?:{sql:string;params?:string[]}) {
        let response:Response;
        try {response=await fetchImpl(apiRoot+path,{method:query?'POST':'GET',redirect:'error',signal:AbortSignal.timeout(15000),
            headers:{Authorization:'Bearer '+token,...(query?{'Content-Type':'application/json'}:{})},...(query?{body:JSON.stringify(query)}:{})});}
        catch {throw Error(label+' request failed; no retry attempted.');}
        if(!response.ok){await response.body?.cancel().catch(()=>{});throw Error(label+' HTTP '+response.status+'; stopping without retry.');}
        let body:Record<string,unknown>;
        try {body=await jsonBody(response);}catch(error){
            // Streaming failures may contain transport URLs or credentials.
            const message=(error as Error).message;
            if(['API response has no body.','API response is too large.','API response is not valid JSON.'].includes(message))throw error;
            throw Error(label+' response could not be read; no retry attempted.');
        }
        if(body.success!==true)throw Error(label+' API response was not successful; stopping without retry.');
        return body.result;
    }
    const subdomain=record(await request('/workers/subdomain','Account subdomain'));
    if(subdomain.subdomain!=='weave-389')throw Error('Account does not own the authorized subdomain.');
    const settings=record(await request('/workers/scripts/demo/settings','Existing Worker demo'));
    if(!Array.isArray(settings.bindings))throw Error('Worker binding metadata is invalid.');
    const bindings=settings.bindings.map(record),matches=bindings.filter(binding=>binding.name==='db'),binding=matches[0];
    if(matches.length!==1||binding.type!=='d1'||!uuid(binding.id))throw Error('Expected one valid existing D1 binding named db.');
    if(bindings.some(value=>!['plain_text','json','secret_text'].includes(String(value.type))&&!(value.type==='d1'&&value.name==='db')))
        throw Error('Unexpected existing resource binding; stopping.');
    const databaseId=binding.id,details=record(await request('/d1/database/'+databaseId,'Existing D1 identity'));
    if(details.uuid!==databaseId||typeof details.name!=='string'||!/^[a-zA-Z0-9_-]+$/.test(details.name))throw Error('Existing D1 identity does not match its binding.');
    const queryEvidence:{rows_written:number;changes:number;changed_db:boolean}[]=[];
    async function query(sql:typeof schemaSql|typeof ledgerSql) {
        const result=await request('/d1/database/'+databaseId+'/query',sql===schemaSql?'Schema metadata':'Migration ledger',
            sql===schemaSql?{sql:schemaSql}:{sql:ledgerSql,params:[scriptId]});
        if(!Array.isArray(result)||result.length!==1||record(result[0]).success!==true)throw Error('D1 query was not successful; stopping without retry.');
        const item=record(result[0]),meta=record(item.meta);
        if(meta.rows_written!==0||meta.changes!==0||meta.changed_db!==false)throw Error('D1 query did not confirm zero writes; stopping.');
        if(!Array.isArray(item.results))throw Error('D1 query rows are invalid.');
        queryEvidence.push({rows_written:0,changes:0,changed_db:false});return item.results.map(record);
    }
    const rows=await query(schemaSql),names=new Set<string>();
    if(rows.length>512)throw Error('Too many schema metadata rows; snapshot is incomplete.');
    const schema=rows.map(row=>{
        if(!['table','index'].includes(String(row.type))||typeof row.name!=='string'||typeof row.tbl_name!=='string'||
            !(row.sql===null||typeof row.sql==='string')||names.has(row.name))throw Error('Invalid or duplicate schema metadata.');
        names.add(row.name);return {type:row.type as string,name:row.name,tbl_name:row.tbl_name,sql:row.sql};
    });
    let status='table-absent',checksum:string|null=null,completedAt:string|null=null,checksumMatches:boolean|null=null;
    const ledgerTable=schema.find(row=>row.type==='table'&&row.name==='data_migrations');
    if(ledgerTable){
        if(typeof ledgerTable.sql!=='string'||!/^\s*CREATE\s+TABLE\b/i.test(ledgerTable.sql))throw Error('Expected an ordinary migration ledger table; stopping.');
        const ledger=await query(ledgerSql);status='record-absent';
        if(ledger.length>1)throw Error('Duplicate migration ledger records; stopping.');
        if(ledger.length){
            const row=ledger[0];
            if(row.script_id!==scriptId||typeof row.checksum!=='string'||!/^[a-f0-9]{64}$/.test(row.checksum)||
                !(row.completed_at===null||typeof row.completed_at==='string'&&/^\d+$/.test(row.completed_at)))throw Error('Invalid migration ledger fields; stopping.');
            checksum=row.checksum;completedAt=row.completed_at as string|null;checksumMatches=checksum===expectedChecksum;
            status=!checksumMatches?'checksum-mismatch':completedAt===null?'incomplete':'completed';
        }
    }
    const report={worker:'demo',binding:'db',database:{uuid:databaseId,name:details.name},checkedAt:new Date().toISOString(),
        sourceSchemaSha256:hash(source),schemaComparison:'not-performed',schema,migration:{scriptId,status,expectedChecksum,checksum,checksumMatches,completedAt},
        readOnly:true,queryEvidence};
    if([token,account!].some(value=>JSON.stringify(report).includes(value)))throw Error('Unexpected credential reflection in metadata; report suppressed.');
    return report;
}

if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try {
        if(process.argv.length!==2)throw Error('D1 preflight does not accept arguments.');
        console.log(JSON.stringify(await inspectD1(),null,2));
    }catch(error){console.error((error as Error).message);process.exitCode=1;}
}
