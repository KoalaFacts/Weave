import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, existsSync } from 'node:fs';
import { deploy, deploymentConfig, noPendingMigrations } from '../scripts/deploy.mjs';
const base=JSON.parse(readFileSync(new URL('../wrangler.jsonc',import.meta.url)));
const database={uuid:'11111111-2222-3333-4444-555555555555',name:'synthetic-weave'};
const env={CLOUDFLARE_ACCOUNT_ID:'a'.repeat(32),CLOUDFLARE_API_TOKEN:'synthetic-deploy-token'};
function fixture({status=200,failAt=0,pending=false,bindingResult=[{name:'db',type:'d1',id:database.uuid}],databaseResult=database,cleanup=['0','0'],verificationStatus=200,extraEnv={}}={}){
    const requests=[],commands=[],paths=[],configs=[];
    return {requests,commands,paths,configs,options:{env:{...env,...extraEnv},fetchImpl:async(url,options)=>{
        requests.push(url);assert.equal(options.redirect,'error');assert.equal(options.headers.Authorization,'Bearer '+env.CLOUDFLARE_API_TOKEN);
        const verifying=commands.length===4;
        const bindings=verifying?[...bindingResult]:bindingResult;
        if(verifying)for(const [index,name] of ['CLEANUP_MAX_MESSAGES','CLEANUP_TIME_BUDGET_MS'].entries()){
            if(cleanup[index]!==undefined)bindings.push({name,type:'plain_text',text:cleanup[index]});
        }
        const result=url.endsWith('/workers/subdomain')?{subdomain:'weave-389'}:url.endsWith('/settings')?{bindings}:databaseResult;
        const responseStatus=verifying?verificationStatus:status;
        return {ok:responseStatus===200,status:responseStatus,json:async()=>({success:true,result})};
    },run:args=>{
        commands.push(args.slice(0,-2));paths.push(args.at(-1));
        const config=readFileSync(args.at(-1),'utf8');assert.ok(!config.includes(env.CLOUDFLARE_API_TOKEN));configs.push(JSON.parse(config));
        return {status:commands.length===failAt?1:0,stdout:commands.length===3?(pending?'0002_pending.sql\n':'No migrations to apply!\n'):''};
    }}};
}
test('deployment configuration targets the existing Worker and db binding',()=>{
    const config=deploymentConfig(base,env);assert.equal(config.name,'demo');assert.equal(config.d1_databases[0].binding,'db');assert.equal(config.workers_dev,true);assert.equal(config.vars.REGISTRATION_ENABLED,'false');assert.equal(config.vars.WEAVE_HOST,undefined);
    assert.throws(()=>deploymentConfig(base,{...env,CLOUDFLARE_ACCOUNT_ID:''}),/CLOUDFLARE_ACCOUNT_ID must identify the existing account/);
});
test('deployment uses checked-in registration and ignores inherited host and registration settings',()=>{
    for(const registration of ['true','false']){
        const config=deploymentConfig({...base,vars:{...base.vars,REGISTRATION_ENABLED:registration}},{...env,WEAVE_REGISTRATION_ENABLED:registration==='true'?'false':'true',WEAVE_HOST:'https://stale.example.test'});
        assert.equal(config.vars.REGISTRATION_ENABLED,registration);
        assert.equal(config.vars.WEAVE_HOST,undefined);
    }
});
test('checked-in demo and deployment keep business cleanup disabled despite inherited values',()=>{
    assert.equal(base.vars.CLEANUP_MAX_MESSAGES,'0');assert.equal(base.vars.CLEANUP_TIME_BUDGET_MS,'0');
    for(const inherited of [{},{CLEANUP_MAX_MESSAGES:'128',CLEANUP_TIME_BUDGET_MS:'2000'},{CLEANUP_MAX_MESSAGES:'invalid',CLEANUP_TIME_BUDGET_MS:'10001'}]){
        const config=deploymentConfig({...base,vars:{...base.vars,CLEANUP_MAX_MESSAGES:'128',CLEANUP_TIME_BUDGET_MS:'2000'}},{...env,...inherited});
        assert.equal(config.vars.CLEANUP_MAX_MESSAGES,'0');assert.equal(config.vars.CLEANUP_TIME_BUDGET_MS,'0');
    }
});
test('deployment passes disabled cleanup to Wrangler and verifies actual Worker settings after deployment',async()=>{
    const f=fixture({extraEnv:{CLEANUP_MAX_MESSAGES:'128',CLEANUP_TIME_BUDGET_MS:'2000'}});await deploy(f.options);assert.equal(f.requests.length,4);
    assert.deepEqual(f.commands,[['d1','migrations','list','db','--remote'],['d1','migrations','apply','db','--remote'],['d1','migrations','list','db','--remote'],['deploy','--keep-vars']]);
    for(const config of f.configs){assert.equal(config.vars.CLEANUP_MAX_MESSAGES,'0');assert.equal(config.vars.CLEANUP_TIME_BUDGET_MS,'0');}
    assert.ok(f.requests.at(-1).endsWith('/workers/scripts/demo/settings'));
    assert.ok(f.paths.every(path=>!existsSync(path)));
});
for(const cleanup of [['128','0'],['0','2000'],['0',undefined]])test(`deployed cleanup ${cleanup} is rejected without retry or rollback`,async()=>{
    const f=fixture({cleanup});await assert.rejects(deploy(f.options),/Deployment completed.*not confirmed disabled/);
    assert.equal(f.commands.length,4);assert.equal(f.requests.length,4);assert.ok(f.paths.every(path=>!existsSync(path)));
});
test('denied post-deployment settings verification reports that deployment already completed',async()=>{
    const f=fixture({verificationStatus:403});await assert.rejects(deploy(f.options),/HTTP 403.*deployment already completed/);
    assert.equal(f.commands.length,4);assert.equal(f.requests.length,4);assert.ok(f.paths.every(path=>!existsSync(path)));
});
for(const [label,response,expected] of [
    ['wrong binding type',{success:true,result:{bindings:[{name:'CLEANUP_MAX_MESSAGES',type:'json',text:'0'},{name:'CLEANUP_TIME_BUDGET_MS',type:'plain_text',text:'0'}]}},/Deployment completed.*CLEANUP_MAX_MESSAGES is not confirmed disabled/],
    ['missing bindings',{success:true,result:{}},/Deployment completed.*not confirmed disabled/],
    ['invalid bindings',{success:true,result:{bindings:{}}},/Deployment completed.*not confirmed disabled/],
    ['invalid result',{success:false},/invalid result.*deployment already completed/],
    ['null result',null,/invalid result.*deployment already completed/],
    ['invalid JSON',undefined,/invalid JSON.*deployment already completed/],
    ['network failure',undefined,/lookup failed.*deployment already completed/],
])test(`post-deployment ${label} stops with an accurate outcome`,async()=>{
    const f=fixture(),fetchImpl=f.options.fetchImpl;
    f.options.fetchImpl=async(...args)=>{
        const result=await fetchImpl(...args);if(f.commands.length!==4)return result;
        if(label==='network failure')throw Error('synthetic connection failure');
        return {...result,json:async()=>{if(label==='invalid JSON')throw Error('synthetic parse failure');return response;}};
    };
    await assert.rejects(deploy(f.options),expected);
    assert.equal(f.commands.length,4);assert.equal(f.requests.length,4);assert.ok(f.paths.every(path=>!existsSync(path)));
});
for(const failAt of [1,2,3,4])test(`command failure ${failAt} stops subsequent operations and cleans config`,async()=>{
    const f=fixture({failAt});await assert.rejects(deploy(f.options),/failed; stopping/);assert.equal(f.commands.length,failAt);assert.ok(f.paths.every(path=>!existsSync(path)));
});
test('remaining or unrecognized migration output blocks deployment',async()=>{
    const f=fixture({pending:true});await assert.rejects(deploy(f.options),/Pending migration state/);assert.equal(f.commands.length,3);
    assert.equal(noPendingMigrations('unknown output'),false);assert.equal(noPendingMigrations('✅ No migrations to apply!\n'),true);
});
for(const status of [401,403,404])test(`HTTP ${status} stops without retry or mutation`,async()=>{
    const f=fixture({status});await assert.rejects(deploy(f.options),new RegExp('HTTP '+status));assert.equal(f.requests.length,1);assert.equal(f.commands.length,0);
});
for(const extraEnv of [{},{WEAVE_D1_DATABASE_ID:'22222222-3333-4444-5555-666666666666',WEAVE_D1_DATABASE_NAME:'stale-ignored'}])test(`deployment discovers existing D1 identity with ${Object.keys(extraEnv).length?'stale ignored':'no'} database settings`,async()=>{
    const f=fixture({extraEnv});await deploy(f.options);
    assert.deepEqual(f.requests.map(url=>url.split('/accounts/')[1].slice(32)),['/workers/subdomain','/workers/scripts/demo/settings','/d1/database/'+database.uuid,'/workers/scripts/demo/settings']);
    assert.equal(f.commands.length,4);
    for(const config of f.configs){
        assert.equal(config.d1_databases.length,1);
        assert.equal(config.d1_databases[0].binding,'db');
        assert.equal(config.d1_databases[0].database_id,database.uuid);
        assert.equal(config.d1_databases[0].database_name,database.name);
    }
});
for(const [label,bindingResult] of [
    ['missing',[]],
    ['invalid collection',{}],
    ['invalid UUID',[{name:'db',type:'d1',id:'../other-resource'}]],
    ['placeholder UUID',[{name:'db',type:'d1',id:'00000000-0000-0000-0000-000000000000'}]],
    ['wrong type',[{name:'db',type:'kv_namespace',id:database.uuid}]],
    ['ambiguous',[{name:'db',type:'d1',id:database.uuid},{name:'db',type:'d1',id:database.uuid}]],
])test(`discovered D1 binding ${label} stops before database lookup and migration`,async()=>{
    const f=fixture({bindingResult});await assert.rejects(deploy(f.options),/existing D1 binding named db/);
    assert.equal(f.requests.length,2);assert.equal(f.commands.length,0);
});
for(const databaseResult of [{...database,uuid:'22222222-3333-4444-5555-666666666666'},{...database,name:''},{...database,name:'../other'}])test(`discovered D1 identity ${JSON.stringify(databaseResult)} stops before migration`,async()=>{
    const f=fixture({databaseResult});await assert.rejects(deploy(f.options),/Existing D1 identity/);
    assert.equal(f.requests.length,3);assert.equal(f.commands.length,0);
});
for(const requestNumber of [2,3])for(const status of [401,403,404])test(`discovery request ${requestNumber} HTTP ${status} stops without retry or mutation`,async()=>{
    const f=fixture(),fetchImpl=f.options.fetchImpl;
    f.options.fetchImpl=async(...args)=>{const response=await fetchImpl(...args);return f.requests.length===requestNumber?{...response,ok:false,status}:response;};
    await assert.rejects(deploy(f.options),new RegExp('HTTP '+status));
    assert.equal(f.requests.length,requestNumber);assert.equal(f.commands.length,0);
});
