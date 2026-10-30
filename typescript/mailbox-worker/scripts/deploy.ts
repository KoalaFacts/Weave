import { readFile, writeFile, mkdtemp, mkdir, rm } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { build } from 'esbuild';
import { createMissingSchema, planLatestSchema, type StorageDatabase } from './latest-schema.ts';
import { prepareCompletedDeployment } from './completed-deployment.ts';
import { verifyCompletedMigration } from './completed-migration.ts';

interface WorkerConfig {
    name: string;
    main: string;
    vars?: Record<string,string>;
    d1_databases: {binding:string;database_id:string;database_name:string;remote?:boolean;[key:string]:unknown}[];
    [key:string]: unknown;
}
interface Binding {name:string;type:string;id?:string;namespace_id?:string;class_name?:string;script_name?:string;text?:unknown;service?:string;environment?:string;entrypoint?:string;}
interface WorkerSettings {bindings:Binding[];}
interface AccessApplication {id?:unknown;name?:unknown;type?:unknown;domain?:unknown;aud?:unknown;oauth_configuration?:{enabled?:unknown};}
interface CommandResult {status:number|null;stdout?:string;stderr?:string;}
interface DatabaseConnection {db:StorageDatabase;close:()=>Promise<void>;}
interface DeployOptions {
    env?: NodeJS.ProcessEnv;
    fetchImpl?: typeof fetch;
    run?: (args:string[])=>CommandResult|Promise<CommandResult>;
    root?: string;
    openDatabase?: (configPath:string)=>Promise<DatabaseConnection>;
    buildBundles?: typeof buildArtifacts;
    verifyMigration?: (db:StorageDatabase)=>Promise<unknown>;
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
// Only the one-time, owner-approved Access trial activation may create it.
const approvedIdentityLinkPolicy={omitUnapprovedOAuthIdentityLinks:false} as const;
const demoOrigin='https://demo.weave-389.workers.dev';
const accessTrialName='Weave Managed OAuth Trial';
const accessTrialDomain='demo.weave-389.workers.dev/access-trial/*';
const accessTrialSettings=['ACCESS_TRIAL_ENABLED','ACCESS_TRIAL_ISSUER','ACCESS_TRIAL_RESOURCE','ACCESS_TRIAL_AUDIENCE'];
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
    await build({absWorkingDir:root,entryPoints:['src/index.ts','src/recovery.ts'],bundle:true,format:'esm',platform:'browser',outdir:'dist',external:['node:crypto','cloudflare:workers'],loader:{'.html':'text','.json':'text','.md':'text'}});
    return Object.fromEntries(await Promise.all(['index','recovery'].map(async name=>[name,createHash('sha256').update(await readFile(join(root,'dist',name+'.js'))).digest('hex')])));
}
export async function deploy({env=process.env,fetchImpl=fetch,run,root=directory,openDatabase=openRemoteDatabase,buildBundles=buildArtifacts,verifyMigration=verifyCompletedMigration,applySchema=createMissingSchema,planSchema=planLatestSchema}: DeployOptions={}){
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
    // Public discovery must be observed from the public Internet, with no
    // Cloudflare control token or mailbox control sent to those origins.
    // Bound response size and deadline, and never reflect a remote body/error.
    async function publicJson(url:string,label:string):Promise<Record<string,unknown>>{
        let response:Response;
        try{response=await fetchImpl(url,{method:'GET',headers:{Accept:'application/json'},
            redirect:'manual',signal:AbortSignal.timeout(3500)});}
        catch{throw Error(label+' unavailable; no cloud mutation inferred.');}
        if(response.status!==200||
            (response.headers.get('Content-Type')??'').split(';')[0].trim().toLowerCase()!=='application/json'){
            await response.body?.cancel().catch(()=>{});
            throw Error(label+' unavailable or not JSON (HTTP '+response.status+').');
        }
        let parsed:unknown;
        try{
            const reader=response.body?.getReader();if(!reader)throw Error('missing body');
            const decoder=new TextDecoder('utf-8',{fatal:true});let raw='',size=0;
            try{
                while(true){const part=await reader.read();if(part.done)break;
                    size+=part.value.byteLength;if(size>8192)throw Error('oversized body');
                    raw+=decoder.decode(part.value,{stream:true});}
                raw+=decoder.decode();
            }finally{await reader.cancel().catch(()=>{});reader.releaseLock();}
            parsed=JSON.parse(raw);
        }catch{throw Error(label+' returned invalid bounded JSON.');}
        if(!parsed||typeof parsed!=='object'||Array.isArray(parsed))
            throw Error(label+' returned invalid JSON metadata.');
        return parsed as Record<string,unknown>;
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
        // This AS must support the exact browser authorization-code/PKCE
        // endpoints our public CIMD client needs. A plausible issuer label
        // alone is insufficient to risk switching the real demo into OAuth.
        const metadata=await publicJson(asIssuer+'/.well-known/oauth-authorization-server',
            'Approved OAuth issuer metadata');
        if(metadata.issuer!==asIssuer||
            metadata.client_id_metadata_document_supported!==true||
            metadata.authorization_response_iss_parameter_supported!==true||
            metadata.authorization_endpoint!==asIssuer+'/authorize'||
            metadata.token_endpoint!==asIssuer+'/oauth/token'||
            !Array.isArray(metadata.response_types_supported)||
            !metadata.response_types_supported.includes('code')||
            !Array.isArray(metadata.grant_types_supported)||
            !metadata.grant_types_supported.includes('authorization_code')||
            !Array.isArray(metadata.code_challenge_methods_supported)||
            !metadata.code_challenge_methods_supported.includes('S256'))
            throw Error('Approved OAuth issuer lacks the expected authorization-code, CIMD and PKCE metadata; no mutation attempted.');
        // The shipped AS exports `default class AuthServer`, not a named RPC export.
        // Cloudflare's default WorkerEntrypoint is selected by omitting `entrypoint`.
        authBinding={name:'AUTH_SERVER',type:'service',service:'weave-auth-demo'};
    }
    // One-time Access Managed OAuth trial activation. The owner names the exact
    // already provisioned Access application; this release
    // reads its audience and team domain, creates the approved identity-link
    // table and enables only the path-scoped /access-trial routes.
    const requestedTrial=env.WEAVE_APPROVED_ACCESS_TRIAL_APPLICATION??'';
    let accessTrial:Record<string,string>|undefined;
    if(requestedTrial!==''){
        if(!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/.test(requestedTrial))
            throw Error('Invalid approved Access trial application; no mutation attempted.');
        if(databaseMode!=='maintain'||firstPartyBootstrap||authBinding)
            throw Error('Access trial activation requires a maintain-mode release without another OAuth binding; no mutation attempted.');
        if(worker.bindings.some(binding=>accessTrialSettings.includes(binding?.name))||
            (oauthFlags.length!==0&&oauthFlags[0].text!=='false'))
            throw Error('Access trial or OAuth identity links are already configured; remove the one-time activation.');
        const app=await inspect<AccessApplication>('/access/apps/'+requestedTrial,'Approved Access trial application');
        const organization=await inspect<{auth_domain?:unknown}>('/access/organizations','Access organization');
        if(app.id!==requestedTrial||app.name!==accessTrialName||app.type!=='self_hosted'||app.domain!==accessTrialDomain||
            app.oauth_configuration?.enabled!==true||typeof app.aud!=='string'||!/^[A-Za-z0-9_-]{16,128}$/.test(app.aud))
            throw Error('Approved Access trial application does not match the isolated trial; no mutation attempted.');
        if(typeof organization.auth_domain!=='string'||!/^[a-z0-9-]+\.cloudflareaccess\.com$/.test(organization.auth_domain))
            throw Error('Access organization team domain is invalid; no mutation attempted.');
        accessTrial={
            ACCESS_TRIAL_ENABLED:'true',
            ACCESS_TRIAL_ISSUER:'https://'+organization.auth_domain,
            ACCESS_TRIAL_RESOURCE:demoOrigin+'/access-trial/mcp',
            ACCESS_TRIAL_AUDIENCE:app.aud,
            OAUTH_IDENTITY_LINKS_ENABLED:'true'
        };
        Object.assign(config.vars,accessTrial);
    }
    const schemaPolicy=accessTrial?approvedIdentityLinkPolicy:demoSchemaPolicy;
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
                Object.assign(config.vars,{[name]:value});
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
        const planDemo=(db:StorageDatabase)=>planSchema(db,schemaPolicy);
        const completed=databaseMode==='completed-only'?await prepareCompletedDeployment(connection.db,planDemo):undefined;
        // Read-only and before maintenance: an unconverted database never pauses the service.
        if(!completed){report.conversion=await verifyMigration(connection.db);await save();}
        phase='maintenance deployment requested';await save();
        await command(['deploy','--keep-vars','--no-bundle'],'maintenance');
        verifySettings(await inspect('/workers/scripts/demo/settings','Maintenance settings',phase),'true');
        phase='maintenance verified; database validation in progress';await save();
        if(completed){
            Object.assign(report,await completed.confirm());await save();
        }else{
            const plan=await planDemo(connection.db);report.schemaPlan=plan;await save();
            await applySchema(connection.db,schemaPolicy);
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
        // Live public metadata (not just Cloudflare settings) is a required
        // success condition of this one-time OAuth rollout. A failure leaves
        // the safe recovery version and an explicit incomplete-release report.
        if(firstPartyBootstrap){
            phase='active settings verified; public OAuth connectivity unconfirmed';await save();
            const origin='https://demo.weave-389.workers.dev';
            const publicResource=await publicJson(origin+'/.well-known/oauth-protected-resource/mcp',
                'Public OAuth resource metadata');
            if(publicResource.resource!==origin+'/mcp'||
                !Array.isArray(publicResource.authorization_servers)||
                publicResource.authorization_servers.length!==1||
                publicResource.authorization_servers[0]!=='https://weave-auth-demo.weave-389.workers.dev'||
                !Array.isArray(publicResource.scopes_supported)||
                !publicResource.scopes_supported.includes('weave:messages.send')||
                !publicResource.scopes_supported.includes('weave:messages.ack'))
                throw Error('Public OAuth resource metadata does not advertise the approved issuer and private messaging scopes.');
            const publicClient=await publicJson(origin+'/oauth-client-metadata.json',
                'Public OAuth CIMD client metadata');
            if(publicClient.client_id!==origin+'/oauth-client-metadata.json'||
                !Array.isArray(publicClient.redirect_uris)||publicClient.redirect_uris.length!==1||
                publicClient.redirect_uris[0]!=='http://127.0.0.1/callback'||
                !Array.isArray(publicClient.grant_types)||!publicClient.grant_types.includes('authorization_code')||
                !Array.isArray(publicClient.response_types)||!publicClient.response_types.includes('code')||
                publicClient.token_endpoint_auth_method!=='none')
                throw Error('Public OAuth CIMD client metadata cannot complete the approved public loopback PKCE flow.');
        }
        if(accessTrial){
            phase='active settings verified; Access trial protection unconfirmed';await save();
            // Served by Cloudflare Access, not this Worker: proves the edge still
            // protects the trial path and names the same team as the origin check.
            const protectedResource=await publicJson(demoOrigin+'/.well-known/cloudflare-access-protected-resource/access-trial/mcp',
                'Access trial protected-resource metadata');
            if(protectedResource.resource!==accessTrial.ACCESS_TRIAL_RESOURCE||protectedResource.protected!==true||
                !Array.isArray(protectedResource.authorization_servers)||
                protectedResource.authorization_servers.length!==1||
                protectedResource.authorization_servers[0]!==accessTrial.ACCESS_TRIAL_ISSUER)
                throw Error('Access trial protected-resource metadata does not match the activated team and resource.');
        }
        phase='active deployment verified';report.finishedAt=new Date().toISOString();await save();
        console.log('Verified deployed demo registration, disabled business cleanup, D1 identity and maintenance exit.');
        return report;
    }catch(error){report.failure=(error as Error).message;await save();throw Error((error as Error).message+' Last confirmed stage: '+phase+'. Do not deploy a pre-conversion version or restore a database snapshot.');}
    finally{try{await connection?.close();}finally{await rm(temporary,{recursive:true,force:true});}}

}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try{await deploy();}catch(error){console.error((error as Error).message);process.exitCode=1;}
}
