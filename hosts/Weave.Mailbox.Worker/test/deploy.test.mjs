import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, existsSync } from 'node:fs';
import { deploy, deploymentConfig, noPendingMigrations } from '../scripts/deploy.mjs';
const base=JSON.parse(readFileSync(new URL('../wrangler.jsonc',import.meta.url)));
const env={CLOUDFLARE_ACCOUNT_ID:'a'.repeat(32),WEAVE_D1_DATABASE_ID:'11111111-2222-3333-4444-555555555555',WEAVE_D1_DATABASE_NAME:'synthetic-weave',CLOUDFLARE_API_TOKEN:'synthetic-deploy-token'};
function fixture({status=200,failAt=0,pending=false,wrongBinding=false}={}){
    const requests=[],commands=[],paths=[];
    return {requests,commands,paths,options:{env,fetchImpl:async(url,options)=>{
        requests.push(url);assert.equal(options.redirect,'error');assert.equal(options.headers.Authorization,'Bearer '+env.CLOUDFLARE_API_TOKEN);
        const result=url.endsWith('/workers/subdomain')?{subdomain:'weave-389'}:url.endsWith('/settings')?{bindings:[{name:'db',type:'d1',id:wrongBinding?'wrong':env.WEAVE_D1_DATABASE_ID}]}:{uuid:env.WEAVE_D1_DATABASE_ID,name:env.WEAVE_D1_DATABASE_NAME};
        return {ok:status===200,status,json:async()=>({success:true,result})};
    },run:args=>{
        commands.push(args.slice(0,-2));paths.push(args.at(-1));
        const config=readFileSync(args.at(-1),'utf8');assert.ok(!config.includes(env.CLOUDFLARE_API_TOKEN));
        return {status:commands.length===failAt?1:0,stdout:commands.length===3?(pending?'0002_pending.sql\n':'No migrations to apply!\n'):''};
    }}};
}
test('deployment configuration binds existing resources and validates optional origin',()=>{
    const config=deploymentConfig(base,env);assert.equal(config.name,'demo');assert.equal(config.d1_databases[0].binding,'db');assert.equal(config.workers_dev,true);assert.equal(config.vars.REGISTRATION_ENABLED,'false');assert.equal(config.vars.WEAVE_HOST,undefined);
    assert.equal(deploymentConfig(base,{...env,CLEANUP_MAX_MESSAGES:'0'}).vars.CLEANUP_MAX_MESSAGES,'0');
    assert.equal(deploymentConfig(base,{...env,WEAVE_HOST:'https://demo.weave-389.workers.dev/'}).vars.WEAVE_HOST,'https://demo.weave-389.workers.dev');
    for(const change of [{CLOUDFLARE_ACCOUNT_ID:''},{WEAVE_D1_DATABASE_ID:'00000000-0000-0000-0000-000000000000'},{WEAVE_D1_DATABASE_NAME:''},{WEAVE_REGISTRATION_ENABLED:'yes'},{CLEANUP_MAX_MESSAGES:'100001'},{CLEANUP_TIME_BUDGET_MS:'10001'},{WEAVE_HOST:'https://example.com/path'},{WEAVE_HOST:'http://example.com'}])assert.throws(()=>deploymentConfig(base,{...env,...change}));
});
test('deployment confirms migration completion before deploying and removes temporary config',async()=>{
    const f=fixture();await deploy(f.options);assert.equal(f.requests.length,3);
    assert.deepEqual(f.commands,[['d1','migrations','list','db','--remote'],['d1','migrations','apply','db','--remote'],['d1','migrations','list','db','--remote'],['deploy','--keep-vars']]);
    assert.ok(f.paths.every(path=>!existsSync(path)));
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
