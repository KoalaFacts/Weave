import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
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
        else if(url.includes('/versions/'))result={id:version,resources:{bindings:[...bindingResult,...Object.entries({...base.vars,MAINTENANCE_MODE:'false'}).map(([name,text])=>({name,type:'plain_text',text}))]}};
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
    assert.equal(JSON.parse(readFileSync(new URL('../test-results/deployment/result.json',import.meta.url),'utf8')).recoveryVersion,undefined);
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

const completedChecksum=createHash('sha256').update(['scripts/data-migrations/normalize-records.ts','scripts/data-migrations/legacy-records.ts','src/time.ts','src/credentials.ts']
    .map(path=>readFileSync(new URL('../'+path,import.meta.url),'utf8')).join('\n')).digest('hex');
async function completedFixture({legacy=false,checksum=completedChecksum,completed='123',record=true}={}){
    const h=await start({legacy}),f=fixture({extraEnv:{WEAVE_DEPLOY_DATABASE_MODE:'completed-only'}}),sql=[];
    if(record&&!legacy)await h.db.prepare('INSERT INTO data_migrations(script_id,checksum,created_at,updated_at,completed_at) VALUES(?,?,1,1,?)')
        .bind('normalize-mailbox-records',checksum,completed).run();
    f.options.openDatabase=async()=>({db:{prepare:query=>{sql.push(query);return h.db.prepare(query);},batch:statements=>h.db.batch(statements)},close:async()=>{f.closed=true;}});
    f.options.planSchema=planLatestSchema;
    f.options.convert=async()=>{throw Error('Unexpected historical conversion');};
    f.options.applySchema=async()=>{throw Error('Unexpected schema application');};
    return Object.assign(f,{h,sql,writes:()=>sql.filter(query=>!query.startsWith('SELECT ')&&!query.startsWith('PRAGMA '))});
}
for(const mode of ['','unknown','COMPLETED-ONLY'])test('invalid explicit database mode stops before external operations: '+JSON.stringify(mode),async()=>{
    const f=fixture({extraEnv:{WEAVE_DEPLOY_DATABASE_MODE:mode}});
    await assert.rejects(deploy(f.options),/database mode/i);assert.equal(f.requests.length,0);assert.equal(f.commands.length,0);
});
for(const [name,options] of [
    ['missing table',{legacy:true}],['missing record',{record:false}],['incomplete record',{completed:null}],['checksum mismatch',{checksum:'b'.repeat(64)}]
])test('completed-only refuses '+name+' before maintenance or writes',async()=>{
    const f=await completedFixture(options);
    try{await assert.rejects(deploy(f.options),/completed migration/i);assert.equal(f.commands.length,0);assert.deepEqual(f.writes(),[]);assert.equal(f.closed,true);}
    finally{await f.h.close();}
});
test('completed-only refuses missing schema objects before maintenance or writes',async()=>{
    const f=await completedFixture();
    try{
        await f.h.db.prepare('DROP INDEX messages_identity').run();
        await assert.rejects(deploy(f.options),/empty schema plan/i);assert.equal(f.commands.length,0);assert.deepEqual(f.writes(),[]);
        assert.equal(await f.h.db.prepare("SELECT count(*) n FROM sqlite_master WHERE name='messages_identity'").first('n'),0);
    }finally{await f.h.close();}
});
for(const stage of ['before-maintenance','maintenance','after-ledger-check','before-active'])test('completed-only refuses a migration write gate at '+stage,async()=>{
    const f=await completedFixture(),run=f.options.run,open=f.options.openDatabase;
    const addGate=()=>f.h.db.prepare("CREATE TRIGGER data_migration_write_gate BEFORE INSERT ON mailbox_guard BEGIN SELECT RAISE(ABORT,'data_migration_in_progress'); END").run();
    try{
        if(stage==='before-maintenance')await addGate();
        f.options.run=async args=>{const result=run(args);if((stage==='maintenance'&&f.commands.length===1)||(stage==='before-active'&&f.commands.length===2))await addGate();return result;};
        if(stage==='after-ledger-check')f.options.openDatabase=async()=>{
            const connection=await open(),batch=connection.db.batch;
            connection.db.batch=async statements=>{const result=await batch(statements);await addGate();return result;};return connection;
        };
        await assert.rejects(deploy(f.options),/migration write gate/i);
        assert.deepEqual(f.events,stage==='before-maintenance'?[]:stage==='before-active'?['maintenance','recovery']:['maintenance']);
        assert.equal(f.writes().length,['before-maintenance','maintenance'].includes(stage)?0:1);
        assert.equal(await f.h.db.prepare("SELECT count(*) n FROM sqlite_master WHERE type='trigger' AND name='data_migration_write_gate'").first('n'),1);
        await assert.rejects(f.h.db.prepare('INSERT INTO mailbox_guard(ok,created_at,updated_at) VALUES(1,1,1)').run(),/data_migration_in_progress/);
    }finally{await f.h.close();}
});
test('completed-only deploys on actual D1 with one ledger check and without invoking conversion or schema writes',async()=>{
    const f=await completedFixture();
    try{
        const before=(await f.h.db.prepare('SELECT * FROM data_migrations').all()).results,revision=(await f.h.db.prepare('SELECT * FROM mailbox_revision').all()).results;
        const result=await deploy(f.options);
        assert.equal(result.databaseMode,'completed-only');assert.equal(result.conversion.alreadyCompleted,true);assert.equal(result.conversion.checksum,completedChecksum);
        assert.deepEqual(f.events,['maintenance','recovery','active']);assert.equal(result.schemaPlan.statements.length,0);
        assert.equal(f.writes().length,1);assert.match(f.writes()[0],/^CREATE TABLE IF NOT EXISTS `data_migrations`/);
        assert.deepEqual((await f.h.db.prepare('SELECT * FROM data_migrations').all()).results,before);
        assert.deepEqual((await f.h.db.prepare('SELECT * FROM mailbox_revision').all()).results,revision);
    }finally{await f.h.close();}
});
for(const kind of ['ledger','schema'])test('completed-only stops after maintenance if '+kind+' changed, before the ledger check',async()=>{
    const f=await completedFixture(),run=f.options.run;
    f.options.run=async args=>{const result=run(args);if(f.commands.length===1)await f.h.db.prepare(kind==='ledger'?'UPDATE data_migrations SET completed_at=NULL':'DROP INDEX messages_identity').run();return result;};
    try{
        await assert.rejects(deploy(f.options),kind==='ledger'?/completed migration/i:/empty schema plan/i);
        assert.deepEqual(f.events,['maintenance']);assert.deepEqual(f.writes(),[]);
    }finally{await f.h.close();}
});
for(const sql of ['DELETE FROM data_migrations','PRAGMA user_version=1','PRAGMA table_info("accounts"); DELETE FROM accounts','SELECT * FROM accounts'])test('completed-only planner cannot dispatch unexpected SQL: '+sql,async()=>{
    const f=await completedFixture();
    f.options.planSchema=async db=>{await db.prepare(sql).all();return {statements:[],preservedUnmanagedTables:[]};};
    try{await assert.rejects(deploy(f.options),/restricted deployment/i);assert.equal(f.commands.length,0);assert.ok(!f.sql.includes(sql));}
    finally{await f.h.close();}
});
test('completed-only metadata statements never expose run or a raw statement after bind',async()=>{
    const f=await completedFixture(),plan=f.options.planSchema;
    f.options.planSchema=async db=>{
        const statement=db.prepare("SELECT name FROM sqlite_master WHERE type='table'");
        assert.equal(statement.run,undefined);assert.equal(statement.bind().run,undefined);
        await assert.rejects(async()=>db.batch([]),/restricted deployment/i);
        return plan(db);
    };
    try{await deploy(f.options);assert.deepEqual(f.events,['maintenance','recovery','active']);}
    finally{await f.h.close();}
});
for(const kind of ['ledger','schema'])test('completed-only stops after the permitted ledger check when '+kind+' changes',async()=>{
    const f=await completedFixture(),open=f.options.openDatabase;
    f.options.openDatabase=async()=>{
        const connection=await open(),batch=connection.db.batch;
        connection.db.batch=async statements=>{const result=await batch(statements);await f.h.db.prepare(kind==='ledger'?'UPDATE data_migrations SET completed_at=NULL':'DROP INDEX messages_identity').run();return result;};
        return connection;
    };
    try{
        await assert.rejects(deploy(f.options),kind==='ledger'?/completed migration/i:/empty schema plan/i);assert.deepEqual(f.events,['maintenance']);
        assert.equal(f.writes().length,1);assert.match(f.writes()[0],/^CREATE TABLE IF NOT EXISTS `data_migrations`/);
    }finally{await f.h.close();}
});
for(const kind of ['ledger','schema'])test('completed-only retains recovery evidence when final '+kind+' validation fails',async()=>{
    const f=await completedFixture(),run=f.options.run;
    f.options.run=async args=>{const result=run(args);if(f.commands.length===2)await f.h.db.prepare(kind==='ledger'?'UPDATE data_migrations SET completed_at=NULL':'DROP INDEX messages_identity').run();return result;};
    try{
        await assert.rejects(deploy(f.options),kind==='ledger'?/completed migration/i:/empty schema plan/i);assert.deepEqual(f.events,['maintenance','recovery']);assert.equal(f.writes().length,1);
        const report=JSON.parse(readFileSync(new URL('../test-results/deployment/result.json',import.meta.url),'utf8'));
        assert.equal(report.recoveryVersion,version);assert.equal(report.recoveryCommand,'wrangler versions deploy '+version+'@100% --yes');
        assert.equal(report.phase,'schema-aware recovery version retained; maintenance retained');assert.equal(report.finishedAt,undefined);
        assert.match(report.failure,kind==='ledger'?/completed migration/i:/empty schema plan/i);
    }finally{await f.h.close();}
});
for(const evidence of ['changed','missing','failed'])test('completed-only requires successful zero-change evidence from the ledger check: '+evidence,async()=>{
    const f=await completedFixture(),open=f.options.openDatabase;
    f.options.openDatabase=async()=>{
        const connection=await open(),batch=connection.db.batch;
        connection.db.batch=async statements=>{const result=await batch(statements);return result.map(item=>evidence==='changed'?{...item,meta:{...item.meta,changed_db:true}}:evidence==='missing'?{...item,meta:undefined}:{...item,success:false});};
        return connection;
    };
    try{await assert.rejects(deploy(f.options),/did not confirm zero changes/);assert.deepEqual(f.events,['maintenance']);assert.equal(f.writes().length,1);}
    finally{await f.h.close();}
});


test('demo maintain deploy leaves optional unapproved OAuth table absent',async()=>{
    const h=await start(),f=fixture();
    try{
        await h.db.prepare('DROP TABLE oauth_identity_links').run();
        f.options.openDatabase=async()=>({db:h.db,close:async()=>{}});
        f.options.convert=async()=>({alreadyCompleted:true});
        f.options.planSchema=planLatestSchema;
        f.options.applySchema=createMissingSchema;
        const result=await deploy(f.options);
        assert.equal(result.schemaPlan.statements.length,0);
        assert.equal(f.commands.length,3);
        assert.equal(await h.db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='oauth_identity_links'").first('name'),null);
    }finally{await h.close();}
});
test('completed-only deploy accepts an intentionally unprovisioned OAuth identity-link table',async()=>{
    const f=await completedFixture();
    try{
        await f.h.db.prepare('DROP TABLE oauth_identity_links').run();
        const result=await deploy(f.options);
        assert.equal(result.databaseMode,'completed-only');
        assert.equal(result.schemaPlan.statements.length,0);
        assert.deepEqual(f.events,['maintenance','recovery','active']);
        assert.equal(await f.h.db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='oauth_identity_links'").first('name'),null);
    }finally{await f.h.close();}
});
test('enabled remote OAuth identity links require provisioned D1 schema before any maintenance',async()=>{
    const h=await start(),f=fixture({bindingResult:[dbBinding,{name:'OAUTH_IDENTITY_LINKS_ENABLED',type:'plain_text',text:'true'}]});
    try{
        await h.db.prepare('DROP TABLE oauth_identity_links').run();
        f.options.openDatabase=async()=>({db:h.db,close:async()=>{}});
        await assert.rejects(deploy(f.options),/OAuth identity links.*schema.*approval|OAuth identity links.*not provisioned/i);
        assert.equal(f.commands.length,0);
        assert.equal(await h.db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='oauth_identity_links'").first('name'),null);
    }finally{await h.close();}
});

const existingAuthService=[
    dbBinding,
    {name:'AUTH_SERVER',type:'service',service:'approved-auth-worker'},
    {name:'OAUTH_ISSUER',type:'plain_text',text:'https://auth.weave.example'},
    {name:'OAUTH_RESOURCE',type:'plain_text',text:'https://demo.weave-389.workers.dev/mcp'},
    {name:'OAUTH_ACCOUNT_PROOF_KEY',type:'secret_text'}
];
test('existing operator-provisioned OAuth Service Binding survives maintenance, recovery and active deploy without provisioning',async()=>{
    const f=fixture({bindingResult:existingAuthService});
    const result=await deploy(f.options);
    assert.equal(result.worker,'demo');
    assert.deepEqual(f.events,['maintenance','convert','schema','recovery','active']);
    for(const config of f.configs){
        assert.deepEqual(config.services,[{binding:'AUTH_SERVER',service:'approved-auth-worker'}]);
        assert.equal(config.vars.OAUTH_ISSUER,undefined,'Do not materialize operator OAuth settings into temporary config');
        assert.equal(config.vars.OAUTH_RESOURCE,undefined,'Do not materialize operator OAuth settings into temporary config');
        assert.equal(JSON.stringify(config).includes('OAUTH_ACCOUNT_PROOF_KEY'),false,'Do not serialize an installed proof key');
    }
    assert.equal(f.commands.length,3);
    assert.equal(f.closed,true);
});
test('lost existing OAuth Service Binding after maintenance stops before any D1 conversion or schema write',async()=>{
    const f=fixture({bindingResult:existingAuthService}),original=f.options.fetchImpl;
    f.options.fetchImpl=async (...args)=>{
        const response=await original(...args);
        if(args[0].endsWith('/settings')&&f.commands.length>0){
            const payload=await response.json();
            payload.result.bindings=payload.result.bindings.filter(binding=>binding.name!=='AUTH_SERVER');
            return {...response,json:async()=>payload};
        }
        return response;
    };
    await assert.rejects(deploy(f.options),/AUTH_SERVER.*not confirmed|OAuth Service Binding.*not confirmed/i);
    assert.deepEqual(f.events,['maintenance']);
    assert.equal(f.closed,true);
});
test('OAuth issuer or account-proof secret lost after maintenance stops before D1 conversion',async()=>{
    for(const missing of ['OAUTH_ISSUER','OAUTH_ACCOUNT_PROOF_KEY']){
        const f=fixture({bindingResult:existingAuthService}),original=f.options.fetchImpl;
        f.options.fetchImpl=async (...args)=>{
            const response=await original(...args);
            if(args[0].endsWith('/settings')&&f.commands.length>0){
                const payload=await response.json();
                payload.result.bindings=payload.result.bindings.filter(binding=>binding.name!==missing);
                return {...response,json:async()=>payload};
            }
            return response;
        };
        await assert.rejects(deploy(f.options),/OAuth.*not confirmed/i);
        assert.deepEqual(f.events,['maintenance']);
    }
});
test('untrusted or malformed Service Bindings cannot be smuggled into a code-only demo release',async()=>{
    for(const bindingResult of [
        [dbBinding,{name:'NOT_OAUTH',type:'service',service:'foreign'}],
        [...existingAuthService.map(b=>b.name==='AUTH_SERVER'?{...b,service:'../bad-target'}:b)],
        [...existingAuthService.map(b=>b.name==='AUTH_SERVER'?{...b,service:'demo'}:b)],
        [...existingAuthService.map(b=>b.name==='AUTH_SERVER'?{...b,environment:'unreviewed'}:b)],
        [...existingAuthService.map(b=>b.name==='AUTH_SERVER'?{...b,entrypoint:'../bad'}:b)]
    ]){
        const f=fixture({bindingResult});
        await assert.rejects(deploy(f.options),/Service Binding|service binding|OAuth|Unexpected existing resource bindings/);
        assert.equal(f.commands.length,0);
    }
});

test('a previously approved OAuth Service Binding can use default or named RPC entrypoint without requiring optional first-party proof',async()=>{
    for(const entrypoint of [undefined,'AuthServer']){
        const bindingResult=existingAuthService.filter(b=>b.name!=='OAUTH_ACCOUNT_PROOF_KEY').map(b=>
            b.name==='AUTH_SERVER'&&entrypoint?{...b,entrypoint}:b);
        const f=fixture({bindingResult});
        await deploy(f.options);
        for(const config of f.configs){
            assert.deepEqual(config.services,[{binding:'AUTH_SERVER',service:'approved-auth-worker',...(entrypoint?{entrypoint}:{})}]);
            assert.ok(!JSON.stringify(config).includes('OAUTH_ACCOUNT_PROOF_KEY'));
        }
        assert.deepEqual(f.events,['maintenance','convert','schema','recovery','active']);
    }
});


const firstPartyIssuer='https://weave-auth-demo.weave-389.workers.dev';
const firstPartyResource='https://demo.weave-389.workers.dev/mcp';
const preapprovedResource=[
    dbBinding,{name:'OAUTH_ACCOUNT_PROOF_KEY',type:'secret_text'}
];
const preapprovedIssuer=[
    {name:'OAUTH_ISSUER',type:'plain_text',text:firstPartyIssuer},
    {name:'OAUTH_RESOURCE',type:'plain_text',text:firstPartyResource},
    {name:'OAUTH_ACCOUNT_PROOF_KEY',type:'secret_text'},
    {name:'OAUTH_KV',type:'kv_namespace',namespace_id:'b'.repeat(32)},
    {name:'CONSENT_NONCES',type:'durable_object_namespace',class_name:'ConsentNonce'}
];
const validFirstPartyIssuerMetadata={
    issuer:firstPartyIssuer,client_id_metadata_document_supported:true,
    code_challenge_methods_supported:['S256'],
    authorization_response_iss_parameter_supported:true,
    authorization_endpoint:firstPartyIssuer+'/authorize',
    token_endpoint:firstPartyIssuer+'/oauth/token',
    response_types_supported:['code'],grant_types_supported:['authorization_code']
};
const validFirstPartyResourceMetadata={
    resource:firstPartyResource,authorization_servers:[firstPartyIssuer],
    scopes_supported:['weave:messages.send','weave:messages.ack']
};
const validFirstPartyClientMetadata={
    client_id:'https://demo.weave-389.workers.dev/oauth-client-metadata.json',
    redirect_uris:['http://127.0.0.1/callback'],token_endpoint_auth_method:'none',
    grant_types:['authorization_code'],response_types:['code']
};
function firstPartyBootstrap({bindings=preapprovedResource,asBindings=preapprovedIssuer,publicStatus=200,
    metadata=validFirstPartyIssuerMetadata,flag='true',resourceStatus=200,clientStatus=200,
    resourceMetadata=validFirstPartyResourceMetadata,clientMetadata=validFirstPartyClientMetadata}={}){
    const f=fixture({bindingResult:bindings,extraEnv:{WEAVE_APPROVED_FIRST_PARTY_OAUTH_BINDING:flag}});
    const original=f.options.fetchImpl;
    f.options.fetchImpl=async (url,options)=>{
        if(url.endsWith('/workers/scripts/weave-auth-demo/settings')){
            f.requests.push(url);
            assert.equal(options.headers.Authorization,'Bearer '+env.CLOUDFLARE_API_TOKEN);
            return {ok:true,status:200,json:async()=>({success:true,result:{bindings:asBindings}})};
        }
        if(url===firstPartyIssuer+'/.well-known/oauth-authorization-server'){
            f.requests.push(url);
            assert.equal(options.headers?.Authorization,undefined,'Never send the Cloudflare API token to the public issuer');
            return Response.json(metadata,{status:publicStatus});
        }
        if(url==='https://demo.weave-389.workers.dev/.well-known/oauth-protected-resource/mcp'){
            f.requests.push(url);
            assert.equal(options.headers?.Authorization,undefined,'Never send a Cloudflare token to public resource metadata');
            return Response.json(resourceMetadata,{status:resourceStatus});
        }
        if(url==='https://demo.weave-389.workers.dev/oauth-client-metadata.json'){
            f.requests.push(url);
            assert.equal(options.headers?.Authorization,undefined,'Never send a Cloudflare token to public client metadata');
            return Response.json(clientMetadata,{status:clientStatus});
        }
        const response=await original(url,options);
        // Mirror Cloudflare's post-upload report; assertions also inspect the
        // actual generated Wrangler configs, not just this simulated response.
        if(f.commands.length && flag==='true' &&
            (url.endsWith('/workers/scripts/demo/settings')||url.includes('/workers/scripts/demo/versions/'))){
            const payload=await response.json(),target=url.includes('/versions/')
                ?payload.result.resources.bindings:payload.result.bindings;
            target.push({name:'AUTH_SERVER',type:'service',service:'weave-auth-demo'});
            for(const [name,text] of [['OAUTH_ISSUER',firstPartyIssuer],['OAUTH_RESOURCE',firstPartyResource]])
                target.push({name,type:'plain_text',text});
            return {...response,json:async()=>payload};
        }
        return response;
    };
    return f;
}
test('explicit one-time first-party bootstrap attaches the approved existing AS in every maintenance/recovery/active stage',async()=>{
    const f=firstPartyBootstrap();
    await deploy(f.options);
    assert.deepEqual(f.events,['maintenance','convert','schema','recovery','active']);
    assert.ok(f.requests.some(url=>url.endsWith('/workers/scripts/weave-auth-demo/settings')));
    assert.ok(f.requests.includes(firstPartyIssuer+'/.well-known/oauth-authorization-server'));
    assert.ok(f.requests.includes('https://demo.weave-389.workers.dev/.well-known/oauth-protected-resource/mcp'),
        'Successful OAuth rollout must prove real resource discovery after activation');
    assert.ok(f.requests.includes('https://demo.weave-389.workers.dev/oauth-client-metadata.json'),
        'Successful OAuth rollout must prove the public CIMD client after activation');
    for(const config of f.configs){
        assert.deepEqual(config.services,[{binding:'AUTH_SERVER',service:'weave-auth-demo'}],
            'The shipped AuthServer is a default export; the service binding MUST NOT select an absent named entrypoint.');
        assert.equal(config.vars.OAUTH_ISSUER,firstPartyIssuer);
        assert.equal(config.vars.OAUTH_RESOURCE,firstPartyResource);
        assert.equal(JSON.stringify(config).includes('OAUTH_ACCOUNT_PROOF_KEY'),false);
    }
    assert.equal(f.closed,true);
});
test('OAuth binding bootstrap fails closed before maintenance when preapproved infrastructure or public issuer is absent',async()=>{
    const cases=[
        firstPartyBootstrap({bindings:[dbBinding]}),
        firstPartyBootstrap({bindings:[...preapprovedResource,{name:'OAUTH_CLIENT_SECRET',type:'secret_text'}]}),
        firstPartyBootstrap({asBindings:preapprovedIssuer.filter(x=>x.name!=='OAUTH_KV')}),
        firstPartyBootstrap({asBindings:preapprovedIssuer.map(x=>x.name==='OAUTH_KV'?{...x,namespace_id:'bad'}:x)}),
        firstPartyBootstrap({asBindings:preapprovedIssuer.filter(x=>x.name!=='CONSENT_NONCES')}),
        firstPartyBootstrap({publicStatus:401}),
        firstPartyBootstrap({metadata:{issuer:'https://untrusted.example',client_id_metadata_document_supported:true,code_challenge_methods_supported:['S256']}}),
        firstPartyBootstrap({metadata:{issuer:firstPartyIssuer,client_id_metadata_document_supported:false,code_challenge_methods_supported:['S256']}}),
        firstPartyBootstrap({flag:'tru'}),
        firstPartyBootstrap({bindings:[...preapprovedResource,{name:'AUTH_SERVER',type:'service',service:'other'}]})
    ];
    for(const f of cases){
        await assert.rejects(deploy(f.options));
        assert.equal(f.commands.length,0,'No maintenance, D1 or active upload may start on a failed preflight');
    }
});
test('OAuth binding bootstrap verifies the new Service Binding immediately after maintenance before writing to D1',async()=>{
    const f=firstPartyBootstrap(),original=f.options.fetchImpl;
    f.options.fetchImpl=async (url,options)=>{
        const response=await original(url,options);
        if(url.endsWith('/workers/scripts/demo/settings')&&f.commands.length===1){
            const payload=await response.json();
            payload.result.bindings=payload.result.bindings.filter(binding=>binding.name!=='AUTH_SERVER');
            return {...response,json:async()=>payload};
        }
        return response;
    };
    await assert.rejects(deploy(f.options),/AUTH_SERVER.*not confirmed|OAuth Service Binding.*not confirmed/i);
    assert.deepEqual(f.events,['maintenance']);
});
test('ordinary demo deploy does not bootstrap even with a preinstalled proof secret',async()=>{
    const f=fixture({bindingResult:preapprovedResource});
    await deploy(f.options);
    assert.ok(f.configs.every(c=>!('services' in c)&&c.vars.OAUTH_ISSUER===undefined));
});

test('approved first-party AS metadata must expose a viable same-origin code and PKCE issuer before any upload',async()=>{
    for(const metadata of [
        {...validFirstPartyIssuerMetadata,authorization_endpoint:undefined},
        {...validFirstPartyIssuerMetadata,authorization_endpoint:'https://foreign.example/authorize'},
        {...validFirstPartyIssuerMetadata,authorization_endpoint:firstPartyIssuer+'/authorize?redirect=evil'},
        {...validFirstPartyIssuerMetadata,token_endpoint:undefined},
        {...validFirstPartyIssuerMetadata,token_endpoint:'http://weave-auth-demo.weave-389.workers.dev/oauth/token'},
        {...validFirstPartyIssuerMetadata,token_endpoint:'https://foreign.example/oauth/token'},
        {...validFirstPartyIssuerMetadata,response_types_supported:['token']},
        {...validFirstPartyIssuerMetadata,grant_types_supported:['refresh_token']},
        {...validFirstPartyIssuerMetadata,authorization_response_iss_parameter_supported:false}
    ]){
        const f=firstPartyBootstrap({metadata});
        await assert.rejects(deploy(f.options),/issuer|authorization|token|PKCE|metadata/i);
        assert.equal(f.commands.length,0,'Bad discovery may not take down a running public demo');
    }
});
test('approved AS binding rollout verifies public resource discovery and CIMD before reporting success',async()=>{
    for(const variant of [
        {resourceStatus:404},
        {resourceMetadata:{...validFirstPartyResourceMetadata,resource:'https://foreign.example/mcp'}},
        {resourceMetadata:{...validFirstPartyResourceMetadata,authorization_servers:['https://foreign.example']}},
        {clientStatus:404},
        {clientMetadata:{...validFirstPartyClientMetadata,client_id:'https://foreign.example/metadata.json'}},
        {clientMetadata:{...validFirstPartyClientMetadata,redirect_uris:['https://other.example/callback']}}
    ]){
        const f=firstPartyBootstrap(variant);
        await assert.rejects(deploy(f.options),/public OAuth|resource metadata|client metadata|CIMD/i);
        assert.equal(f.commands.length,3,'Public metadata check runs only after versioned deployment and setting verification');
        assert.equal(f.closed,true);
    }
});
