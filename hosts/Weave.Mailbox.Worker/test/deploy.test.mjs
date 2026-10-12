import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, existsSync } from 'node:fs';
import { deploy, deploymentConfig, noPendingMigrations } from '../scripts/deploy.mjs';
const base=JSON.parse(readFileSync(new URL('../wrangler.jsonc',import.meta.url)));
const env={CLOUDFLARE_ACCOUNT_ID:'a'.repeat(32),WEAVE_D1_DATABASE_ID:'11111111-2222-3333-4444-555555555555',WEAVE_D1_DATABASE_NAME:'synthetic-weave',CLOUDFLARE_API_TOKEN:'synthetic-deploy-token'};
function fixture({status=200,failAt=0,pending=false,wrongBinding=false,cleanup=['0','0'],verificationStatus=200,extraEnv={}}={}){
    const requests=[],commands=[],paths=[],configs=[];
    return {requests,commands,paths,configs,options:{env:{...env,...extraEnv},fetchImpl:async(url,options)=>{
        requests.push(url);assert.equal(options.redirect,'error');assert.equal(options.headers.Authorization,'Bearer '+env.CLOUDFLARE_API_TOKEN);
        const verifying=commands.length===4;
        const bindings=[{name:'db',type:'d1',id:wrongBinding?'wrong':env.WEAVE_D1_DATABASE_ID}];
        if(verifying)for(const [index,name] of ['CLEANUP_MAX_MESSAGES','CLEANUP_TIME_BUDGET_MS'].entries()){
            if(cleanup[index]!==undefined)bindings.push({name,type:'plain_text',text:cleanup[index]});
        }
        const result=url.endsWith('/workers/subdomain')?{subdomain:'weave-389'}:url.endsWith('/settings')?{bindings}:{uuid:env.WEAVE_D1_DATABASE_ID,name:env.WEAVE_D1_DATABASE_NAME};
        const responseStatus=verifying?verificationStatus:status;
        return {ok:responseStatus===200,status:responseStatus,json:async()=>({success:true,result})};
    },run:args=>{
        commands.push(args.slice(0,-2));paths.push(args.at(-1));
        const config=readFileSync(args.at(-1),'utf8');assert.ok(!config.includes(env.CLOUDFLARE_API_TOKEN));configs.push(JSON.parse(config));
        return {status:commands.length===failAt?1:0,stdout:commands.length===3?(pending?'0002_pending.sql\n':'No migrations to apply!\n'):''};
    }}};
}
test('deployment configuration binds existing resources and validates optional origin',()=>{
    const config=deploymentConfig(base,env);assert.equal(config.name,'demo');assert.equal(config.d1_databases[0].binding,'db');assert.equal(config.workers_dev,true);assert.equal(config.vars.REGISTRATION_ENABLED,'false');assert.equal(config.vars.WEAVE_HOST,undefined);
    assert.equal(deploymentConfig(base,{...env,WEAVE_HOST:'https://demo.weave-389.workers.dev/'}).vars.WEAVE_HOST,'https://demo.weave-389.workers.dev');
    for(const change of [{CLOUDFLARE_ACCOUNT_ID:''},{WEAVE_D1_DATABASE_ID:'00000000-0000-0000-0000-000000000000'},{WEAVE_D1_DATABASE_NAME:''},{WEAVE_REGISTRATION_ENABLED:'yes'},{WEAVE_HOST:'https://example.com/path'},{WEAVE_HOST:'http://example.com'}])assert.throws(()=>deploymentConfig(base,{...env,...change}));
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
test('existing binding mismatch stops before migrations',async()=>{
    const f=fixture({wrongBinding:true});await assert.rejects(deploy(f.options),/binding does not match/);assert.equal(f.commands.length,0);
});
