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
interface Binding {name:string;type:string;id?:string;namespace_id?:string;class_name?:string;script_name?:string;text?:unknown;service?:string;environment?:string;entrypoint?:string;}
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
// Ordinary demo releases are NOT permission to create an opt-in OAuth identity store.
const demoSchemaPolicy={omitUnapprovedOAuthIdentityLinks:true} as const;
export function deploymentConfig(base: WorkerConfig,env: NodeJS.ProcessEnv,root=directory){
    if(base.name!=='demo'||base.d1_databases?.length!==1||base.d1_databases[0].binding!=='db')throw Error('Expected existing Worker demo and binding db.');
    if(base.services!==undefined)throw Error('OAuth Service Bindings must be provisioned separately by an operator, not by this generic release.');
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
    const base=JSON.parse(await readFile(join(root,'wrangler.jsonc'),'utf8'));
    const config: ReturnType<typeof deploymentConfig> & {
        services?: {binding:string;service:string;entrypoint?:string}[]
    } = deploymentConfig(base,env,root);
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
    if(!Array.isArray(worker.bindings))throw Error('Expected one valid existing D1 binding named db.');
    const bindings=Array.isArray(worker.bindings)?worker.bindings.filter(binding=>binding?.name==='db'):[];
    const oauthFlags=Array.isArray(worker.bindings)?worker.bindings.filter(binding=>binding?.name==='OAUTH_IDENTITY_LINKS_ENABLED'):[];
    if(oauthFlags.length>1||oauthFlags.some(binding=>binding.type!=='plain_text'||(binding.text!=='true'&&binding.text!=='false')))
        throw Error('Invalid existing OAuth identity links setting; no mutation attempted.');
    // A previously approved private OAuth AS may already be bound by an
    // operator. Recreate only that exact existing service binding for all
    // three Wrangler stages; --keep-vars preserves variables, not services.
    // Never introduce a new service target or copy a signing secret to config.
    const authBindings=worker.bindings.filter(binding=>binding?.name==='AUTH_SERVER');
    if(authBindings.length>1)throw Error('Ambiguous existing AUTH_SERVER OAuth Service Binding; no mutation attempted.');
    let authBinding=authBindings[0];
    const requestedBootstrap=env.WEAVE_APPROVED_FIRST_PARTY_OAUTH_BINDING;
    if(requestedBootstrap!==undefined&&!['true','false'].includes(requestedBootstrap))
        throw Error('Invalid first-party OAuth bootstrap permission; no mutation attempted.');
    const firstPartyBootstrap=requestedBootstrap==='true';
    if(firstPartyBootstrap){
        if(authBinding)throw Error('First-party OAuth bootstrap already has a Service Binding; remove the one-time opt-in.');
        const asIssuer='https://weave-auth-demo.weave-389.workers.dev';
        const resource='https://demo.weave-389.workers.dev/mcp';
        const single=(bindings:Binding[],name:string,type:string,value?:string)=>{
            const matches=bindings.filter(binding=>binding?.name===name);
            return matches.length===1&&matches[0].type===type&&(value===undefined||matches[0].text===value);
        };
        // Install the proof secret separately beforehand, but stage issuer,
        // resource and AUTH_SERVER together in the maintenance upload. Installing
        // only issuer/resource on the live Worker would make OAuth fail closed.
        if(!single(worker.bindings,'OAUTH_ACCOUNT_PROOF_KEY','secret_text')||
            ['OAUTH_ISSUER','OAUTH_RESOURCE','OAUTH_INTROSPECTION_URL','OAUTH_CLIENT_ID',
             'OAUTH_CLIENT_SECRET','OAUTH_INTROSPECTION_TRUSTED_ORIGIN']
                .some(name=>worker.bindings.some(binding=>binding?.name===name))||
            (oauthFlags.length!==0&&oauthFlags[0].text!=='false'))
            throw Error('Resource OAuth bootstrap secrets or account policy are not preapproved; no mutation attempted.');
        const as=await inspect<WorkerSettings>('/workers/scripts/weave-auth-demo/settings','Approved existing OAuth authorization Worker');
        const kv=as.bindings?.filter(binding=>binding?.name==='OAUTH_KV')??[];
        const nonce=as.bindings?.filter(binding=>binding?.name==='CONSENT_NONCES')??[];
        if(!Array.isArray(as.bindings)||
            !single(as.bindings,'OAUTH_ISSUER','plain_text',asIssuer)||
            !single(as.bindings,'OAUTH_RESOURCE','plain_text',resource)||
            !single(as.bindings,'OAUTH_ACCOUNT_PROOF_KEY','secret_text')||
            kv.length!==1||kv[0].type!=='kv_namespace'||
            !/^[a-f0-9]{32}$/i.test(kv[0].namespace_id??'')||
            nonce.length!==1||nonce[0].type!=='durable_object_namespace'||
            nonce[0].class_name!=='ConsentNonce'||nonce[0].environment!==undefined||
            (nonce[0].script_name!==undefined&&nonce[0].script_name!=='weave-auth-demo'))
            throw Error('Approved OAuth authorization Worker has incomplete live bindings; no mutation attempted.');
        // Public issuer discovery is a separate anonymous GET. Never forward
        // Cloudflare credentials to the AS; refuse redirects and large bodies.
        let response:Response;
        try{response=await fetchImpl(asIssuer+'/.well-known/oauth-authorization-server',{
            method:'GET',headers:{Accept:'application/json'},redirect:'manual',signal:AbortSignal.timeout(3500)
        });}catch{throw Error('Approved OAuth issuer is not publicly reachable; no mutation attempted.');}
        if(response.status!==200||
            (response.headers.get('Content-Type')??'').split(';')[0].trim().toLowerCase()!=='application/json')
            throw Error('Approved OAuth issuer discovery is unavailable; no mutation attempted.');
        let metadata:unknown;
        try{
            const reader=response.body?.getReader();if(!reader)throw Error('missing body');
            const decoder=new TextDecoder('utf-8',{fatal:true});let raw='',size=0;
            try{
                while(true){const part=await reader.read();if(part.done)break;
                    size+=part.value.byteLength;if(size>8192)throw Error('oversized metadata');
                    raw+=decoder.decode(part.value,{stream:true});}
                raw+=decoder.decode();
            }finally{await reader.cancel().catch(()=>{});reader.releaseLock();}
            metadata=JSON.parse(raw);
        }catch{throw Error('Approved OAuth issuer metadata is invalid; no mutation attempted.');}
        if(!metadata||typeof metadata!=='object'||Array.isArray(metadata)||
            (metadata as Record<string,unknown>).issuer!==asIssuer||
            (metadata as Record<string,unknown>).client_id_metadata_document_supported!==true||
            !Array.isArray((metadata as Record<string,unknown>).code_challenge_methods_supported)||
            !(metadata as {code_challenge_methods_supported:string[]}).code_challenge_methods_supported.includes('S256'))
            throw Error('Approved OAuth issuer lacks expected identity, CIMD or PKCE support; no mutation attempted.');
        authBinding={name:'AUTH_SERVER',type:'service',service:'weave-auth-demo',entrypoint:'AuthServer'};
    }
    const oauthExisting=new Map<string,Binding>();
    if(authBinding){
        if(authBinding.type!=='service'||typeof authBinding.service!=='string'||
            !/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(authBinding.service)||
            authBinding.service==='demo'||authBinding.environment!==undefined||
            (authBinding.entrypoint!==undefined&&!/^[A-Za-z_$][A-Za-z0-9_$]{0,127}$/.test(authBinding.entrypoint)))
            throw Error('Invalid existing AUTH_SERVER OAuth Service Binding; no mutation attempted.');
        for(const [name,type] of [
            ['OAUTH_ISSUER','plain_text'],['OAUTH_RESOURCE','plain_text']
        ]){
            if(firstPartyBootstrap){
                const value=name==='OAUTH_ISSUER'
                    ?'https://weave-auth-demo.weave-389.workers.dev'
                    :'https://demo.weave-389.workers.dev/mcp';
                oauthExisting.set(name,{name,type,text:value});
                config.vars[name]=value;
            }else{
                const found=worker.bindings.filter(binding=>binding?.name===name);
                if(found.length!==1||found[0].type!==type)throw Error('Incomplete existing OAuth Service Binding; no mutation attempted.');
                oauthExisting.set(name,found[0]);
            }
        }
        // The first-party proof route is opt-in independently of the trusted
        // authorization Service Binding. Preserve a pre-existing signing secret
        // without requiring this provider-specific ownership ceremony.
        const proofs=worker.bindings.filter(binding=>binding?.name==='OAUTH_ACCOUNT_PROOF_KEY');
        if(proofs.length>1||proofs.some(binding=>binding.type!=='secret_text'))
            throw Error('Invalid existing OAuth account-proof key binding; no mutation attempted.');
        if(proofs.length===1)oauthExisting.set('OAUTH_ACCOUNT_PROOF_KEY',proofs[0]);
        let issuer:URL;
        try{issuer=new URL(oauthExisting.get('OAUTH_ISSUER')!.text as string);}
        catch{throw Error('Invalid existing OAuth issuer; no mutation attempted.');}
        if(issuer.protocol!=='https:'||issuer.username||issuer.password||issuer.search||issuer.hash||
            typeof oauthExisting.get('OAUTH_ISSUER')!.text!=='string'||
            oauthExisting.get('OAUTH_RESOURCE')!.text!=='https://demo.weave-389.workers.dev/mcp')
            throw Error('Invalid existing OAuth resource or issuer; no mutation attempted.');
        if(['OAUTH_INTROSPECTION_URL','OAUTH_CLIENT_ID','OAUTH_CLIENT_SECRET','OAUTH_INTROSPECTION_TRUSTED_ORIGIN']
            .some(name=>worker.bindings.some(binding=>binding.name===name)))
            throw Error('Mixed OAuth Service Binding and RFC7662 introspection settings; no mutation attempted.');
        config.services=[{binding:'AUTH_SERVER',service:authBinding.service,...(authBinding.entrypoint?{entrypoint:authBinding.entrypoint}:{})}];
    }
    const binding=bindings[0];
    if(bindings.length!==1||binding?.type!=='d1'||!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(binding.id??'')||binding.id==='00000000-0000-0000-0000-000000000000')throw Error('Expected one valid existing D1 binding named db.');
    if(worker.bindings.some(binding=>!['plain_text','json','secret_text'].includes(binding?.type)&&!(binding?.type==='d1'&&binding.name==='db')&&binding!==authBinding))throw Error('Unexpected existing resource bindings; refusing to overwrite them.');
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
            const foundAuth=deployed.bindings.filter(binding=>binding?.name==='AUTH_SERVER');
            if(authBinding){
                if(foundAuth.length!==1||foundAuth[0].type!=='service'||foundAuth[0].service!==authBinding.service||
                    foundAuth[0].entrypoint!==authBinding.entrypoint||foundAuth[0].environment!==undefined)
                    throw Error('Existing AUTH_SERVER OAuth Service Binding not confirmed; stopping before schema writes.');
                for(const [name,before] of oauthExisting){
                    const now=deployed.bindings.filter(binding=>binding?.name===name);
                    if(now.length!==1||now[0].type!==before.type||
                        (before.type==='plain_text'&&now[0].text!==before.text))
                        throw Error('Existing OAuth setting '+name+' not confirmed; stopping before schema writes.');
                }
            }else if(foundAuth.length)throw Error('Unexpected new AUTH_SERVER OAuth Service Binding; refusing the release.');

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
        // A previously enabled link resolver must not be deployed against an
        // absent identity store. Never silently create that store as part of
        // the generic demo release or bypass its separate schema approval.
        if(oauthFlags[0]?.text==='true'&&!tables.includes('oauth_identity_links'))
            throw Error('OAuth identity links schema not provisioned; separate storage approval required before deployment.');
        const planDemo=(db:StorageDatabase)=>planSchema(db,demoSchemaPolicy);
        const completed=databaseMode==='completed-only'?await prepareCompletedDeployment(connection.db,root,planDemo):undefined;
        phase='maintenance deployment requested';await save();
        await command(['deploy','--keep-vars','--no-bundle'],'maintenance');
        verifySettings(await inspect('/workers/scripts/demo/settings','Maintenance settings',phase),'true');
        phase='maintenance verified; database validation in progress';await save();
        if(completed){
            Object.assign(report,await completed.confirm());await save();
        }else{
            report.conversion=await convert(connection.db);
            const plan=await planDemo(connection.db);report.schemaPlan=plan;await save();
            await applySchema(connection.db,demoSchemaPolicy);
            if((await planDemo(connection.db)).statements.length)throw Error('Schema verification left unapplied statements; maintenance retained.');
        }
        phase='database validated; maintenance retained; uploading recovery';await save();
        const output=await command(['versions','upload','--keep-vars','--no-bundle','--tag','schema-recovery'],'recovery');
        const version=/Worker Version ID:\s*([a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})/i.exec(output)?.[1];
        if(!version)throw Error('Recovery version identifier missing; maintenance retained.');
        const recovery=await inspect<{id:string;resources:WorkerSettings}>('/workers/scripts/demo/versions/'+version,'Recovery version',phase);
        if(recovery.id!==version)throw Error('Recovery version identity mismatch; maintenance retained.');
        verifySettings(recovery.resources,'false');
        report.recoveryVersion=version;
        report.recoveryCommand='wrangler versions deploy '+version+'@100% --yes';
        phase='schema-aware recovery version retained; maintenance retained';await save();
        if(completed)await completed.verify();
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
