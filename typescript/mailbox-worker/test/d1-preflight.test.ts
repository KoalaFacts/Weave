import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const script=new URL('../scripts/d1-preflight.ts',import.meta.url);
const account='a'.repeat(32),token='synthetic-preflight-token';
const database={uuid:'11111111-2222-3333-4444-555555555555',name:'synthetic-weave'};
const binding={name:'db',type:'d1',id:database.uuid};
const checksum='ecd612f0528cc03d0dee877269a5546e3531cd4d3533325fc3441938407efe62';
const schemaSql="SELECT type,name,tbl_name,sql FROM sqlite_master WHERE type IN ('table','index') ORDER BY type,name LIMIT 513";
const ledgerSql="SELECT script_id,checksum,CAST(completed_at AS TEXT) AS completed_at FROM data_migrations WHERE script_id = ? LIMIT 2";
const schema=[{type:'table',name:'data_migrations',tbl_name:'data_migrations',sql:'CREATE TABLE data_migrations(script_id TEXT PRIMARY KEY,checksum TEXT,completed_at INTEGER)'},
    {type:'index',name:'sqlite_autoindex_data_migrations_1',tbl_name:'data_migrations',sql:null}];
const ledger=[{script_id:'normalize-mailbox-records',checksum,completed_at:'639276480000000001'}];
const meta={rows_written:0,changes:0,changed_db:false};

function fixture(change=(body:any,index:number)=>body){
    const requests:{url:string;options:RequestInit}[]=[];
    const bodies=[{subdomain:'weave-389'},{bindings:[binding,{name:'PRIVATE',type:'secret_text',text:'DO-NOT-PRINT'}]},database,
        [{success:true,results:schema,meta}],[{success:true,results:ledger,meta}]];
    const fetchImpl=async(url:string,options:RequestInit)=>{
        requests.push({url,options});const index=requests.length-1;
        const changed=change({success:true,result:structuredClone(bodies[index])},index);
        return changed instanceof Response?changed:Response.json(changed);
    };
    return {requests,options:{env:{CLOUDFLARE_ACCOUNT_ID:account,CLOUDFLARE_API_TOKEN:token},fetchImpl}};
}
async function inspect(options:any){
    assert.ok(existsSync(script),'Independent read-only D1 preflight entry is missing');
    return (await import(script.href)).inspectD1(options);
}

test('preflight reads only the verified demo binding and two fixed SELECTs, preserving int64 completion',async()=>{
    const f=fixture(),result=await inspect(f.options),root='https://api.cloudflare.com/client/v4/accounts/'+account;
    assert.deepEqual(f.requests.map(r=>[r.url,r.options.method]),[
        [root+'/workers/subdomain','GET'],[root+'/workers/scripts/demo/settings','GET'],[root+'/d1/database/'+database.uuid,'GET'],
        [root+'/d1/database/'+database.uuid+'/query','POST'],[root+'/d1/database/'+database.uuid+'/query','POST']]);
    assert.deepEqual(f.requests.slice(3).map(r=>JSON.parse(r.options.body as string)),[{sql:schemaSql},{sql:ledgerSql,params:['normalize-mailbox-records']}]);
    for(const {options} of f.requests){assert.equal(options.redirect,'error');assert.ok(options.signal instanceof AbortSignal);assert.equal((options.headers as any).Authorization,'Bearer '+token);}
    assert.deepEqual(result.database,database);assert.deepEqual(result.schema,schema);
    assert.equal(result.migration.status,'completed');assert.equal(result.migration.expectedChecksum,checksum);
    assert.equal(result.migration.completedAt,'639276480000000001');assert.equal(result.migration.checksumMatches,true);
    assert.match(result.sourceSchemaSha256,/^[a-f0-9]{64}$/);assert.equal(result.schemaComparison,'not-performed');
    assert.equal(result.readOnly,true);assert.deepEqual(result.queryEvidence,[meta,meta]);
    assert.ok(!JSON.stringify(result).includes('DO-NOT-PRINT'));assert.ok(!JSON.stringify(result).includes(token));
});

for(const stage of [0,1,2,3,4])for(const status of [401,403,429,500])test(`HTTP ${status} at stage ${stage} stops immediately and does not expose the response body`,async()=>{
    const f=fixture((body,index)=>index===stage?new Response(token+' DO-NOT-PRINT',{status}):body);
    await assert.rejects(inspect(f.options),error=>{assert.match(error.message,new RegExp('HTTP '+status));assert.ok(!error.message.includes(token));assert.ok(!error.message.includes('DO-NOT-PRINT'));return true;});
    assert.equal(f.requests.length,stage+1);
});
for(const stage of [0,1,2,3,4])test(`API refusal at stage ${stage} is terminal and sanitized`,async()=>{
    const f=fixture((body,index)=>index===stage?{success:false,errors:[{message:token+' DO-NOT-PRINT'}]}:body);
    await assert.rejects(inspect(f.options),/API response was not successful/);assert.equal(f.requests.length,stage+1);
});
test('transport errors are sanitized and never retried',async()=>{
    let calls=0;await assert.rejects(inspect({env:fixture().options.env,fetchImpl:async()=>{calls++;throw Error(token);}}),/request failed/);assert.equal(calls,1);
});
test('redirect responses are rejected without following their target',async()=>{
    const f=fixture(()=>new Response(null,{status:302,headers:{location:'https://outside.example/'}}));
    await assert.rejects(inspect(f.options),/HTTP 302/);assert.equal(f.requests.length,1);
});
test('unbounded and invalid JSON responses fail before a subsequent call',async()=>{
    for(const body of ['not-json '+token,'x'.repeat(262145)]){
        const f=fixture(()=>new Response(body));await assert.rejects(inspect(f.options),/response (is too large|is not valid JSON)/);assert.equal(f.requests.length,1);
    }
});
for(const result of [{subdomain:'other'},{subdomain:'weave-389/other'}])test('a different subdomain cannot select a target '+result.subdomain,async()=>{
    const f=fixture((body,index)=>index===0?{success:true,result}:body);await assert.rejects(inspect(f.options),/authorized subdomain/);assert.equal(f.requests.length,1);
});
for(const bindings of [[],[binding,binding],[{...binding,id:'../other'}],[{...binding,id:'00000000-0000-0000-0000-000000000000'}],[{...binding,type:'kv_namespace'}],[binding,{name:'other',type:'d1',id:database.uuid}]])test('invalid or extra resource bindings cannot query a database '+JSON.stringify(bindings),async()=>{
    const f=fixture((body,index)=>index===1?{success:true,result:{bindings}}:body);await assert.rejects(inspect(f.options),/binding/);assert.equal(f.requests.length,2);
});
for(const result of [{...database,uuid:'22222222-3333-4444-5555-666666666666'},{...database,name:'../other'}])test('D1 metadata must match the discovered binding '+JSON.stringify(result),async()=>{
    const f=fixture((body,index)=>index===2?{success:true,result}:body);await assert.rejects(inspect(f.options),/D1 identity/);assert.equal(f.requests.length,3);
});
test('missing authentication stops before any request',async()=>{
    for(const env of [{},{CLOUDFLARE_ACCOUNT_ID:account},{CLOUDFLARE_ACCOUNT_ID:'../other',CLOUDFLARE_API_TOKEN:token}]){
        const f=fixture();await assert.rejects(inspect({...f.options,env}),/CLOUDFLARE_/);assert.equal(f.requests.length,0);
    }
});
test('absent migration table is reported without a ledger query or initialization',async()=>{
    const f=fixture((body,index)=>{if(index===3)body.result[0].results=[];return body;});
    const result=await inspect(f.options);assert.equal(result.migration.status,'table-absent');assert.equal(f.requests.length,4);
});
test('a virtual table cannot impersonate the migration ledger',async()=>{
    const f=fixture((body,index)=>{if(index===3)body.result[0].results[0].sql='CREATE VIRTUAL TABLE data_migrations USING unexpected_module';return body;});
    await assert.rejects(inspect(f.options),/ordinary migration ledger table/);assert.equal(f.requests.length,4);
});
for(const [rows,status,matches] of [[[],'record-absent',null],[[{...ledger[0],completed_at:null}],'incomplete',true],[[{...ledger[0],checksum:'f'.repeat(64)}],'checksum-mismatch',false]])test('migration state is explicit: '+status,async()=>{
    const f=fixture((body,index)=>{if(index===4)body.result[0].results=rows;return body;});
    const result=await inspect(f.options);assert.equal(result.migration.status,status);assert.equal(result.migration.checksumMatches,matches);
});
for(const rows of [[ledger[0],ledger[0]],[{...ledger[0],script_id:'other'}],[{...ledger[0],completed_at:639276480000000001}],[{...ledger[0],checksum:'invalid'}]])test('malformed or duplicate ledger cannot be reported as completed '+JSON.stringify(rows),async()=>{
    const f=fixture((body,index)=>{if(index===4)body.result[0].results=rows;return body;});await assert.rejects(inspect(f.options),/migration ledger/);
});
for(const badMeta of [undefined,{...meta,rows_written:1},{...meta,changed_db:true},{...meta,changes:1}])test('missing or nonzero query write evidence stops the next query '+JSON.stringify(badMeta),async()=>{
    const f=fixture((body,index)=>{if(index===3)body.result[0].meta=badMeta;return body;});await assert.rejects(inspect(f.options),/zero writes/);assert.equal(f.requests.length,4);
});
test('query-level refusal stops even when the API envelope succeeds',async()=>{
    const f=fixture((body,index)=>{if(index===3)body.result[0].success=false;return body;});await assert.rejects(inspect(f.options),/query was not successful/);assert.equal(f.requests.length,4);
});
for(const rows of [Array.from({length:513},()=>schema[0]),[{...schema[0],type:'view'}],[{...schema[0],sql:12}],[schema[0],schema[0]]])test('invalid or truncated schema is never reported as a complete snapshot '+rows.length,async()=>{
    const f=fixture((body,index)=>{if(index===3)body.result[0].results=rows;return body;});await assert.rejects(inspect(f.options),/schema metadata/);assert.equal(f.requests.length,4);
});
test('unexpected fields are projected away, and a reflected token in allowed metadata is rejected',async()=>{
    const f=fixture((body,index)=>{if(index===3)body.result[0].results[0].unexpected='PRIVATE-BUSINESS-ROW';if(index===4)body.result[0].results[0].unexpected='PRIVATE-BUSINESS-ROW';return body;});
    assert.ok(!JSON.stringify(await inspect(f.options)).includes('PRIVATE-BUSINESS-ROW'));
    const reflected=fixture((body,index)=>{if(index===3)body.result[0].results[0].sql+=' '+token;return body;});
    await assert.rejects(inspect(reflected.options),/credential reflection/);
});
test('CLI rejects arbitrary SQL, targets, and paths before authentication',()=>{
    assert.ok(existsSync(script),'Independent read-only D1 preflight entry is missing');
    const result=spawnSync(process.execPath,[script.pathname,'--sql','DELETE FROM accounts'],{encoding:'utf8',env:{PATH:process.env.PATH}});
    assert.equal(result.status,1);assert.match(result.stderr,/does not accept arguments/);assert.equal(result.stdout,'');
});
