import { readFile, writeFile, mkdtemp, mkdir, rm } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { build } from 'esbuild';
import { normalizeRecords } from './run-data-migration.ts';
import { createMissingSchema, planLatestSchema, type StorageDatabase } from './latest-schema.ts';
import { prepareCompletedDeployment } from './completed-deployment.ts';

interface WorkerConfig {
    name: string;
    main: string;
    vars?: Record<string,string>;
    d1_databases: {binding:string;database_id:string;database_name:string;remote?:boolean;[key:string]:unknown}[];
    [key:string]: unknown;
}
interface Binding {name:string;type:string;id?:string;text?:unknown;}
interface WorkerSettings {bindings:Binding[];}
interface CommandResult {status:number|null;stdout?:string;stderr?:string;}
interface DatabaseConnection {db:StorageDatabase;close:()=>Promise<void>;}
interface DeployOptions {
    env?: NodeJS.ProcessEnv;
    fetchImpl?: typeof fetch;
    run?: (args:string[])=>CommandResult|Promise<CommandResult>;
    root?: string;
    openDatabase?: (configPath:string)=>Promise<DatabaseConnection>;
    buildBundles?: typeof buildArtifacts;
    convert?: (db:StorageDatabase)=>Promise<unknown>;
    applySchema?: typeof createMissingSchema;
    planSchema?: typeof planLatestSchema;
}
interface DeploymentReport {
    worker:string;databaseId:string;artifactSha256:Record<string,string>;startedAt:string;
    databaseMode:'maintain'|'completed-only';
    conversion?:unknown;schemaPlan?:Awaited<ReturnType<typeof planLatestSchema>>;
    recoveryVersion?:string;recoveryCommand?:string;finishedAt?:string;failure?:string;
}

const directory=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const expectedSubdomain='weave-389';
export function deploymentConfig(base: WorkerConfig,env: NodeJS.ProcessEnv,root=directory){
    if(base.name!=='demo'||base.d1_databases?.length!==1||base.d1_databases[0].binding!=='db')throw Error('Expected existing Worker demo and binding db.');
    if(!/^[a-f0-9]{32}$/i.test(env.CLOUDFLARE_ACCOUNT_ID??''))throw Error('CLOUDFLARE_ACCOUNT_ID must identify the existing account.');
    // Business sweeps stay disabled for this demo, including with inherited environment values.
    const vars={...base.vars,CLEANUP_MAX_MESSAGES:'0',CLEANUP_TIME_BUDGET_MS:'0'};
    return {...base,account_id:env.CLOUDFLARE_ACCOUNT_ID,main:resolve(root,base.main),workers_dev:true,vars,
        d1_databases:[{...base.d1_databases[0]}]};
}
export async function openRemoteDatabase(configPath: string){
    const {getPlatformProxy}=await import('wrangler');
    const proxy=await getPlatformProxy<{db:StorageDatabase}>({configPath,envFiles:[],persist:false,remoteBindings:true});
    return {db:proxy.env.db,close:()=>proxy.dispose()};
}
export function runWrangler(args: string[],root: string,env: NodeJS.ProcessEnv){
    return new Promise<CommandResult>((resolve,reject)=>{
        const child=spawn(process.execPath,[join(root,'node_modules/wrangler/bin/wrangler.js'),...args],{cwd:root,env:{...env,CI:'true',NO_COLOR:'1',WRANGLER_SEND_METRICS:'false'},stdio:['ignore','pipe','pipe']});
        let stdout='',stderr='',size=0;
        const retain=(which: 'out'|'err')=>(chunk: Buffer)=>{size+=chunk.length;if(size>16*1024*1024){child.kill();return;}if(which==='out')stdout+=chunk;else stderr+=chunk;};
        child.stdout.on('data',retain('out'));child.stderr.on('data',retain('err'));child.on('error',reject);
        child.on('close',status=>resolve({status:size>16*1024*1024?1:status,stdout,stderr}));
    });
}
export async function buildArtifacts(root: string){
    await build({absWorkingDir:root,entryPoints:['src/index.ts','src/recovery.ts'],bundle:true,format:'esm',platform:'browser',outdir:'dist',external:['node:crypto'],loader:{'.html':'text','.json':'text','.md':'text'}});
    return Object.fromEntries(await Promise.all(['index','recovery'].map(async name=>[name,createHash('sha256').update(await readFile(join(root,'dist',name+'.js'))).digest('hex')])));
}
export async function deploy({env=process.env,fetchImpl=fetch,run,root=directory,openDatabase=openRemoteDatabase,buildBundles=buildArtifacts,convert=normalizeRecords,applySchema=createMissingSchema,planSchema=planLatestSchema}: DeployOptions={}){
    const databaseMode=env.WEAVE_DEPLOY_DATABASE_MODE??'maintain';
    if(databaseMode!=='maintain'&&databaseMode!=='completed-only')throw Error('Unknown database mode; use maintain or completed-only.');
    const base=JSON.parse(await readFile(join(root,'wrangler.jsonc'),'utf8')),config=deploymentConfig(base,env,root);
    if(!env.CLOUDFLARE_API_TOKEN)throw Error('CLOUDFLARE_API_TOKEN is required; its value is never written to config.');
    const apiRoot='https://api.cloudflare.com/client/v4/accounts/'+config.account_id;
    async function inspect<T>(path: string,label: string,state='no mutation attempted'): Promise<T>{
        let response;try{response=await fetchImpl(apiRoot+path,{headers:{Authorization:'Bearer '+env.CLOUDFLARE_API_TOKEN},redirect:'error'});}catch{throw Error(label+' lookup failed; '+state+'.');}
        if(!response.ok)throw Error(label+' lookup failed (HTTP '+response.status+'); '+state+'.');
        let body:{success?:boolean;result?:T};try{body=await response.json() as typeof body;}catch{throw Error(label+' lookup returned invalid JSON; '+state+'.');}if(body?.success!==true||!body.result)throw Error(label+' lookup returned an invalid result; '+state+'.');return body.result;
    }
    const subdomain=await inspect<{subdomain:string}>('/workers/subdomain','Account subdomain');
    if(subdomain.subdomain!==expectedSubdomain)throw Error('Account does not own the authorized weave-389.workers.dev subdomain.');
    const worker=await inspect<WorkerSettings>('/workers/scripts/demo/settings','Existing Worker demo');
    const bindings=Array.isArray(worker.bindings)?worker.bindings.filter(binding=>binding?.name==='db'):[];
    const binding=bindings[0];
    if(bindings.length!==1||binding?.type!=='d1'||!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(binding.id??'')||binding.id==='00000000-0000-0000-0000-000000000000')throw Error('Expected one valid existing D1 binding named db.');
    if(worker.bindings.some(binding=>!['plain_text','json','secret_text'].includes(binding?.type)&&!(binding?.type==='d1'&&binding.name==='db')))throw Error('Unexpected existing resource bindings; refusing to overwrite them.');
    const database=await inspect<{uuid:string;name:string}>('/d1/database/'+binding.id,'Existing D1 database');
    if(database.uuid!==binding.id||typeof database.name!=='string'||!/^[a-zA-Z0-9_-]+$/.test(database.name))throw Error('Existing D1 identity does not match its Worker binding or has an invalid name.');
    config.d1_databases[0].database_id=database.uuid;
    config.d1_databases[0].database_name=database.name;
    const hashes=await buildBundles(root),temporary=await mkdtemp(join(root,'.deploy-'));
    const evidence=join(root,'test-results','deployment');await mkdir(evidence,{recursive:true});
    let connection: DatabaseConnection|undefined,phase='verified resources; no deployment attempted';
    const report: DeploymentReport={worker:'demo',databaseId:database.uuid,artifactSha256:hashes,startedAt:new Date().toISOString(),databaseMode};
    const save=async()=>writeFile(join(evidence,'result.json'),JSON.stringify({...report,phase},null,2)+'\n');
    try{
        const configs: Record<string,string>={};
        for(const [name,entry,maintenance] of [['maintenance','index','true'],['active','index','false'],['recovery','recovery','false']]){
            configs[name]=join(temporary,name+'.json');
            await writeFile(configs[name],JSON.stringify({...config,main:join(root,'dist',entry+'.js'),vars:{...config.vars,MAINTENANCE_MODE:maintenance}},null,2),{mode:0o600});
        }
        const proxyConfig=join(temporary,'database.json');
        await writeFile(proxyConfig,JSON.stringify({...config,d1_databases:[{...config.d1_databases[0],remote:true}]},null,2),{mode:0o600});
        const execute=run??(args=>runWrangler(args,root,env));
        async function command(args: string[],kind: string){
            const result=await execute([...args,'--config',configs[kind]]);
            if(result.stdout)process.stdout.write(result.stdout);if(result.stderr)process.stderr.write(result.stderr);
            if(result.status!==0)throw Error('Wrangler '+args.slice(0,3).join(' ')+' failed; stopping before the next operation.');
            return result.stdout??'';
        }
        function verifySettings(deployed: WorkerSettings,maintenance: string){
            if(!Array.isArray(deployed.bindings))throw Error('Worker settings have invalid bindings; stopping.');
            for(const [name,value] of Object.entries({...config.vars,MAINTENANCE_MODE:maintenance})){
                const found=deployed.bindings.filter(binding=>binding?.name===name);
                if(found.length!==1||found[0].type!=='plain_text'||found[0].text!==value)throw Error('Worker '+name+' is not confirmed against configuration; stopping.');
            }
            const found=deployed.bindings.filter(binding=>binding?.name==='db');
            if(found.length!==1||found[0].type!=='d1'||found[0].id!==database.uuid)throw Error('Worker D1 binding changed; stopping.');
        }
        connection=await openDatabase(proxyConfig);
        // Read-only proof that this is an existing supported database. No implicit
        // initialization, resource provisioning or legacy SQL migration chain.
        const tables=(await connection.db.prepare("SELECT name FROM sqlite_master WHERE type='table'").all<{name:string}>()).results.map(row=>row.name);
        if(!tables.includes('mailbox_records')||!tables.includes('mailbox_revision')||!tables.includes('mailbox_guard'))throw Error('Expected existing mailbox storage; no mutation attempted.');
        const completed=databaseMode==='completed-only'?await prepareCompletedDeployment(connection.db,root,planSchema):undefined;
        phase='maintenance deployment requested';await save();
        await command(['deploy','--keep-vars','--no-bundle'],'maintenance');
        verifySettings(await inspect('/workers/scripts/demo/settings','Maintenance settings',phase),'true');
        phase='maintenance verified; database validation in progress';await save();
        if(completed){
            Object.assign(report,await completed.confirm());await save();
        }else{
            report.conversion=await convert(connection.db);
            const plan=await planSchema(connection.db);report.schemaPlan=plan;await save();
            await applySchema(connection.db);
            if((await planSchema(connection.db)).statements.length)throw Error('Schema verification left unapplied statements; maintenance retained.');
        }
        phase='database validated; maintenance retained; uploading recovery';await save();
        const output=await command(['versions','upload','--keep-vars','--no-bundle','--tag','schema-recovery'],'recovery');
        const version=/Worker Version ID:\s*([a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})/i.exec(output)?.[1];
        if(!version)throw Error('Recovery version identifier missing; maintenance retained.');
        const recovery=await inspect<{id:string;resources:WorkerSettings}>('/workers/scripts/demo/versions/'+version,'Recovery version',phase);
        if(recovery.id!==version)throw Error('Recovery version identity mismatch; maintenance retained.');
        verifySettings(recovery.resources,'false');
        if(completed)await completed.verify();
        report.recoveryVersion=version;
        report.recoveryCommand='wrangler versions deploy '+version+'@100% --yes';
        phase='schema-aware recovery version retained; active deployment requested';await save();
        console.log('Schema-aware read-only recovery version: '+version+' (SHA-256 '+hashes.recovery+').');
        await command(['deploy','--keep-vars','--no-bundle'],'active');
        phase='active deployment completed; settings unverified';await save();
        verifySettings(await inspect('/workers/scripts/demo/settings','Worker settings',phase),'false');
        phase='active deployment verified';report.finishedAt=new Date().toISOString();await save();
        console.log('Verified deployed demo registration, disabled business cleanup, D1 identity and maintenance exit.');
        return report;
    }catch(error){report.failure=(error as Error).message;await save();throw Error((error as Error).message+' Last confirmed stage: '+phase+'. Do not deploy a pre-conversion version or restore a database snapshot.');}
    finally{try{await connection?.close();}finally{await rm(temporary,{recursive:true,force:true});}}

}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try{await deploy();}catch(error){console.error((error as Error).message);process.exitCode=1;}
}
