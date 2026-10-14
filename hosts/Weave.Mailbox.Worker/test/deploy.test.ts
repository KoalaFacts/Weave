import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, existsSync } from 'node:fs';
import { deploy, deploymentConfig } from '../scripts/deploy.ts';
import { start } from './harness.ts';
import { normalizeRecords } from '../scripts/run-data-migration.ts';
import { createMissingSchema, planLatestSchema } from '../scripts/latest-schema.ts';
const base=JSON.parse(readFileSync(new URL('../wrangler.jsonc',import.meta.url)));
const database={uuid:'11111111-2222-3333-4444-555555555555',name:'synthetic-weave'};
const version='aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const env={CLOUDFLARE_ACCOUNT_ID:'a'.repeat(32),CLOUDFLARE_API_TOKEN:'synthetic-deploy-token'};
const dbBinding={name:'db',type:'d1',id:database.uuid};
function fixture({status=200,failAt=0,bindingResult=[dbBinding],databaseResult=database,settings={},extraEnv={}}={}){
    const requests=[],commands=[],paths=[],configs=[],events=[];
    const f={requests,commands,paths,configs,events,closed:false};
    f.options={env:{...env,...extraEnv},fetchImpl:async(url,options)=>{
        requests.push(url);assert.equal(options.redirect,'error');assert.equal(options.headers.Authorization,'Bearer '+env.CLOUDFLARE_API_TOKEN);
        let result;
        if(url.endsWith('/workers/subdomain'))result={subdomain:'weave-389'};
        else if(url.includes('/versions/'))result={id:version,resources:{bindings:[dbBinding,...Object.entries({...base.vars,MAINTENANCE_MODE:'false'}).map(([name,text])=>({name,type:'plain_text',text}))]}};
        else if(url.endsWith('/settings'))result={bindings:commands.length?[...bindingResult,...Object.entries({...base.vars,MAINTENANCE_MODE:commands.length===1?'true':'false',...settings}).filter(([,value])=>value!==undefined).map(([name,text])=>({name,type:'plain_text',text}))]:bindingResult};
        else result=databaseResult;
        return {ok:status===200,status,json:async()=>({success:true,result})};
    },run:args=>{
        commands.push(args.slice(0,-2));paths.push(args.at(-1));events.push(commands.length===1?'maintenance':commands.length===2?'recovery':'active');
        const config=readFileSync(args.at(-1),'utf8');assert.ok(!config.includes(env.CLOUDFLARE_API_TOKEN));configs.push(JSON.parse(config));
        return {status:commands.length===failAt?1:0,stdout:commands.length===2?'Worker Version ID: '+version+'\n':''};
    },buildBundles:async()=>({index:'a'.repeat(64),recovery:'b'.repeat(64)}),openDatabase:async path=>{
        const config=JSON.parse(readFileSync(path));assert.equal(config.d1_databases[0].remote,true);assert.equal(config.d1_databases[0].database_id,database.uuid);
        assert.ok(!JSON.stringify(config).includes(env.CLOUDFLARE_API_TOKEN));
        return {db:{prepare:()=>({all:async()=>({results:['mailbox_records','mailbox_revision','mailbox_guard'].map(name=>({name}))})})},close:async()=>{f.closed=true;}};
    },convert:async()=>{events.push('convert');return {counts:{accounts:2},historyDigest:'synthetic'};},planSchema:async()=>({statements:[],preservedUnmanagedTables:['d1_migrations']}),applySchema:async()=>{events.push('schema');}};
    return f;
}
test('deployment configuration targets only the existing Worker and retains demo limits',()=>{
    const config=deploymentConfig(base,env);assert.equal(config.name,'demo');assert.equal(config.d1_databases[0].binding,'db');assert.equal(config.workers_dev,true);assert.equal(config.vars.REGISTRATION_MAX_ACCOUNTS,'100');assert.equal(config.vars.REGISTRATION_PER_IP_PER_HOUR,'10');assert.equal(config.vars.REGISTRATION_GLOBAL_PER_HOUR,'100');assert.equal(config.vars.WEAVE_HOST,undefined);assert.equal(config.d1_databases[0].migrations_dir,undefined);
    assert.throws(()=>deploymentConfig(base,{...env,CLOUDFLARE_ACCOUNT_ID:''}),/CLOUDFLARE_ACCOUNT_ID/);
});
test('checked-in registration and disabled cleanup override inherited settings',()=>{
    assert.equal(base.vars.CLEANUP_MAX_MESSAGES,'0');assert.equal(base.vars.CLEANUP_TIME_BUDGET_MS,'0');
    const config=deploymentConfig({...base,vars:{...base.vars,CLEANUP_MAX_MESSAGES:'128',CLEANUP_TIME_BUDGET_MS:'2000'}},{...env,WEAVE_HOST:'https://stale.example.test',WEAVE_REGISTRATION_ENABLED:'false',CLEANUP_MAX_MESSAGES:'128'});
    assert.equal(config.vars.CLEANUP_MAX_MESSAGES,'0');assert.equal(config.vars.CLEANUP_TIME_BUDGET_MS,'0');assert.equal(config.vars.REGISTRATION_ENABLED,'true');assert.equal(config.vars.WEAVE_HOST,undefined);
});
test('deployment pauses all work, converts independently, verifies schema, retains recovery before activating',async()=>{
    const f=fixture();const result=await deploy(f.options);
    assert.deepEqual(f.events,['maintenance','convert','schema','recovery','active']);
    assert.deepEqual(f.commands,[['deploy','--keep-vars','--no-bundle'],['versions','upload','--keep-vars','--no-bundle','--tag','schema-recovery'],['deploy','--keep-vars','--no-bundle']]);
    assert.equal(f.requests.length,6);assert.equal(result.recoveryVersion,version);assert.match(result.recoveryCommand,/@100% --yes$/);
    assert.deepEqual(f.configs.map(c=>c.vars.MAINTENANCE_MODE),['true','false','false']);assert.ok(f.configs[1].main.endsWith('/dist/recovery.js'));
    assert.ok(f.paths.every(path=>!existsSync(path)));assert.equal(f.closed,true);
});
for(const name of Object.keys({...base.vars,MAINTENANCE_MODE:'true'}))for(const value of ['unexpected',undefined])test(`unverified maintenance ${name} ${value??'missing'} prevents conversion`,async()=>{
    const f=fixture({settings:{[name]:value}});await assert.rejects(deploy(f.options),/not confirmed against configuration/);assert.deepEqual(f.events,['maintenance']);assert.equal(f.closed,true);
});
test('unverified active settings reports the completed deployment and never selects the old schema',async()=>{
    const f=fixture(),fetch=f.options.fetchImpl;f.options.fetchImpl=async(...args)=>{const r=await fetch(...args);return f.commands.length===3?{...r,ok:false,status:403}:r;};
    await assert.rejects(deploy(f.options),/HTTP 403.*active deployment completed/);assert.equal(f.commands.length,3);assert.equal(f.closed,true);
});
for(const failAt of [1,2,3])test(`command failure ${failAt} stops subsequent operations and preserves the reported stage`,async()=>{
    const f=fixture({failAt});await assert.rejects(deploy(f.options),/failed; stopping.*Last confirmed stage/);assert.equal(f.commands.length,failAt);assert.ok(f.paths.every(path=>!existsSync(path)));assert.equal(f.closed,true);
});
for(const stage of ['convert','applySchema','planSchema'])test(`${stage} failure leaves maintenance and never uploads active code`,async()=>{
    const f=fixture();f.options[stage]=async()=>{throw Error('synthetic interrupted '+stage);};
    await assert.rejects(deploy(f.options),/synthetic interrupted/);assert.equal(f.commands.length,1);assert.equal(f.closed,true);
});
test('missing recovery version prevents active deployment',async()=>{
    const f=fixture(),run=f.options.run;f.options.run=args=>({...run(args),stdout:''});await assert.rejects(deploy(f.options),/Recovery version identifier missing/);assert.equal(f.commands.length,2);
});
test('recovery version API identity mismatch prevents active deployment',async()=>{
    const f=fixture(),fetch=f.options.fetchImpl;f.options.fetchImpl=async(...args)=>{const r=await fetch(...args);return args[0].includes('/versions/')?{...r,json:async()=>({success:true,result:{id:'other'}})}:r;};
    await assert.rejects(deploy(f.options),/Recovery version identity mismatch/);assert.equal(f.commands.length,2);
});
for(const status of [401,403,404])test(`HTTP ${status} stops without retry or mutation`,async()=>{
    const f=fixture({status});await assert.rejects(deploy(f.options),new RegExp('HTTP '+status));assert.equal(f.requests.length,1);assert.equal(f.commands.length,0);
});
for(const bindingResult of [[],{},[{...dbBinding,id:'../other'}],[{...dbBinding,id:'00000000-0000-0000-0000-000000000000'}],[{...dbBinding,type:'kv_namespace'}],[dbBinding,dbBinding]])test('invalid or ambiguous D1 discovery stops before mutation '+JSON.stringify(bindingResult),async()=>{
    const f=fixture({bindingResult});await assert.rejects(deploy(f.options),/existing D1 binding named db/);assert.equal(f.requests.length,2);assert.equal(f.commands.length,0);
});
for(const databaseResult of [{...database,uuid:'22222222-3333-4444-5555-666666666666'},{...database,name:''},{...database,name:'../other'}])test('database identity mismatch stops before mutation '+JSON.stringify(databaseResult),async()=>{
    const f=fixture({databaseResult});await assert.rejects(deploy(f.options),/Existing D1 identity/);assert.equal(f.commands.length,0);
});
for(const requestNumber of [2,3])for(const status of [401,403,404])test(`discovery ${requestNumber} HTTP ${status} stops without retry`,async()=>{
    const f=fixture(),fetch=f.options.fetchImpl;f.options.fetchImpl=async(...args)=>{const r=await fetch(...args);return f.requests.length===requestNumber?{...r,ok:false,status}:r;};
    await assert.rejects(deploy(f.options),new RegExp('HTTP '+status));assert.equal(f.requests.length,requestNumber);assert.equal(f.commands.length,0);
});
test('actual D1 migration runs through deployment orchestration while preserving historical migration rows',async()=>{
    const h=await start({legacy:true}),f=fixture();
    try{
        await h.db.prepare("CREATE TABLE d1_migrations(id INTEGER PRIMARY KEY,name TEXT,applied_at TEXT)").run();
        await h.db.prepare("INSERT INTO d1_migrations VALUES(1,'0001_mailbox.sql','2026-10-01')").run();
        f.options.openDatabase=async()=>({db:h.db,close:async()=>{}});f.options.convert=normalizeRecords;f.options.applySchema=createMissingSchema;f.options.planSchema=planLatestSchema;
        const result=await deploy(f.options);assert.equal(result.conversion.alreadyCompleted,false);assert.equal(result.schemaPlan.statements.length,0);
        assert.equal(await h.db.prepare('SELECT name FROM d1_migrations').first('name'),'0001_mailbox.sql');
        assert.equal(await h.db.prepare('SELECT count(*) n FROM data_migrations WHERE completed_at IS NOT NULL').first('n'),1);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM sqlite_master WHERE type='trigger'").first('n'),0);
    }finally{await h.close();}
});
