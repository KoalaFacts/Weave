import {spawn} from 'node:child_process';
import {writeFile} from 'node:fs/promises';
import {dirname, join, resolve} from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';
import {renderDemoConfig} from './prepare-demo.ts';
import {validSigningKey} from '../src/human-session.ts';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const issuer='https://weave-auth-demo.weave-389.workers.dev';
const resource='https://demo.weave-389.workers.dev/mcp';
const secretNames=['GITHUB_CLIENT_SECRET','HUMAN_SESSION_KEY','WEAVE_TRIAL_GITHUB_SUBJECTS'] as const;
type Binding={name:string;type:string;database_id?:string;id?:string;namespace_id?:string;class_name?:string;
    service?:string;entrypoint?:string;environment?:string;text?:unknown};
type Settings={bindings:Binding[]};

export function validateIssuerDeployment(env:NodeJS.ProcessEnv){
    const manifest=renderDemoConfig(env);
    if(!env.CLOUDFLARE_API_TOKEN)throw Error('Existing CLOUDFLARE_API_TOKEN is required.');
    if(typeof env.GITHUB_CLIENT_SECRET!=='string'||env.GITHUB_CLIENT_SECRET.length<16)
        throw Error('An installed GitHub OAuth app secret is required.');
    if(!validSigningKey(env.HUMAN_SESSION_KEY??''))
        throw Error('A canonical independently generated 32-byte human-session secret is required.');
    if(env.WEAVE_APPROVED_ISSUER_DEPLOY!=='true')
        throw Error('One-time protected issuer deployment approval flag is required.');
    return manifest;
}

// All preflight calls are GET. In particular, neither D1 /query nor D1 restore
// is needed to publish this separate authorization Worker.
export async function preflightIssuerDeployment(env:NodeJS.ProcessEnv,request:typeof fetch=fetch){
    const manifest=validateIssuerDeployment(env);
    const base='https://api.cloudflare.com/client/v4/accounts/'+env.CLOUDFLARE_ACCOUNT_ID;
    async function get<T>(path:string,label:string,allowAbsent=false):Promise<T|null>{
        let response:Response;
        try{response=await request(base+path,{
            method:'GET',headers:{Authorization:'Bearer '+env.CLOUDFLARE_API_TOKEN},
            redirect:'error',signal:AbortSignal.timeout(10000)
        });}catch{throw Error(label+' lookup unavailable; no mutation attempted.');}
        if(allowAbsent&&response.status===404)return null;
        if(!response.ok)throw Error(label+' lookup denied (HTTP '+response.status+'); no mutation attempted.');
        let json:{success?:boolean;result?:T};
        try{json=await response.json() as typeof json;}catch{
            throw Error(label+' returned invalid JSON; no mutation attempted.');
        }
        if(json?.success!==true||!json.result)throw Error(label+' returned an invalid result; no mutation attempted.');
        return json.result;
    }
    const subdomain=await get<{subdomain:string}>('/workers/subdomain','Workers account');
    if(subdomain?.subdomain!=='weave-389')
        throw Error('The Cloudflare account does not own the fixed Weave Workers.dev subdomain.');
    const demo=await get<Settings>('/workers/scripts/demo/settings','Existing demo Worker');
    if(!Array.isArray(demo?.bindings)||
        demo.bindings.filter(b=>b.name==='db'&&b.type==='d1'&&
            /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(b.database_id??b.id??'')).length!==1||
        demo.bindings.some(b=>b.name==='AUTH_SERVER'))
        throw Error('Existing demo D1 binding or OAuth resource state requires separate review.');
    const kvId=env.WEAVE_AUTH_KV_NAMESPACE_ID!;
    const kv=await get<{id:string}>('/storage/kv/namespaces/'+kvId,'Approved existing OAuth KV namespace');
    if(kv?.id!==kvId)throw Error('OAuth KV namespace identity mismatch; no mutation attempted.');
    const existing=await get<Settings>('/workers/scripts/weave-auth-demo/settings','Fixed OAuth Worker name',true);
    if(existing!==null)throw Error('OAuth Worker already exists; no blind replacement or secret rotation is allowed.');
    return manifest;
}

function requiredBinding(bindings:Binding[],name:string,type:string,extra:(b:Binding)=>boolean=()=>true){
    return bindings.filter(b=>b.name===name).length===1
        &&bindings.some(b=>b.name===name&&b.type===type&&extra(b));
}
export function validateIssuerReadback(bindings:Binding[],env:NodeJS.ProcessEnv){
    if(!Array.isArray(bindings)||
        !requiredBinding(bindings,'OAUTH_ISSUER','plain_text',b=>b.text===issuer)||
        !requiredBinding(bindings,'OAUTH_RESOURCE','plain_text',b=>b.text===resource)||
        !requiredBinding(bindings,'GITHUB_CLIENT_ID','plain_text',b=>b.text===env.GITHUB_CLIENT_ID)||
        !requiredBinding(bindings,'OAUTH_KV','kv_namespace',b=>b.namespace_id===env.WEAVE_AUTH_KV_NAMESPACE_ID)||
        !requiredBinding(bindings,'CONSENT_NONCES','durable_object_namespace',b=>b.class_name==='ConsentNonce'&&b.environment===undefined)||
        !requiredBinding(bindings,'MAILBOX','service',b=>b.service==='demo'&&b.entrypoint==='HumanDirectory'&&(b.environment===undefined||b.environment==='production'))||
        secretNames.some(name=>!requiredBinding(bindings,name,'secret_text')))
        throw Error('Deployed OAuth Worker binding or secret metadata mismatch.');
}

async function cloudReadback(env:NodeJS.ProcessEnv,request:typeof fetch){
    const url='https://api.cloudflare.com/client/v4/accounts/'+env.CLOUDFLARE_ACCOUNT_ID+
        '/workers/scripts/weave-auth-demo/settings';
    const response=await request(url,{method:'GET',redirect:'error',
        headers:{Authorization:'Bearer '+env.CLOUDFLARE_API_TOKEN},signal:AbortSignal.timeout(10000)});
    if(!response.ok)throw Error('Published OAuth Worker settings readback denied (HTTP '+response.status+').');
    const json=await response.json() as {success?:boolean;result?:Settings};
    if(json.success!==true||!json.result)throw Error('Published OAuth Worker readback was invalid.');
    validateIssuerReadback(json.result.bindings,env);
}

// Wrangler gets only the Cloudflare token. The three trial/authentication secrets
// are uploaded together through "secret bulk" stdin, never command arguments, config, stdout or stderr.
async function wrangler(args:string[],env:NodeJS.ProcessEnv,secret?:string){
    const childEnv={...env,CI:'true',NO_COLOR:'1',WRANGLER_SEND_METRICS:'false'};
    for(const name of secretNames)delete childEnv[name];
    return new Promise<void>((done,reject)=>{
        const child=spawn(process.execPath,[join(root,'node_modules/wrangler/bin/wrangler.js'),...args],{
            cwd:root,env:childEnv,stdio:['pipe','ignore','ignore']
        });
        child.on('error',()=>reject(Error('Wrangler could not start; check Node24 and locked installation.')));
        child.on('close',code=>code===0?done():reject(Error('Wrangler command failed; review protected run metadata without exposing secrets.')));
        child.stdin.on('error',()=>{});
        child.stdin.end(secret===undefined?'':secret+'\n');
    });
}

export async function deployApprovedIssuer(env:NodeJS.ProcessEnv=process.env,request:typeof fetch=fetch){
    const manifest=await preflightIssuerDeployment(env,request);
    const filename=join(root,'wrangler.demo.generated.jsonc');
    await writeFile(filename,JSON.stringify(manifest,null,2)+'\n',{flag:'wx',mode:0o600});
    await wrangler(['deploy','--config','wrangler.demo.generated.jsonc','--keep-vars'],env);
    // A single bulk upload avoids three successive publicly deployed partial-secret versions.
    await wrangler(['secret','bulk','--config','wrangler.demo.generated.jsonc'],env,
        JSON.stringify(Object.fromEntries(secretNames.map(name=>[name,env[name]!]))));
    await cloudReadback(env,request);
    // Metadata proves protocol readiness, never actual GitHub login or consent.
    const metadata=await request(issuer+'/.well-known/oauth-authorization-server',{
        method:'GET',redirect:'error',headers:{Accept:'application/json'},signal:AbortSignal.timeout(10000)
    });
    if(metadata.status!==200)throw Error('OAuth Worker published but issuer discovery did not pass (HTTP '+metadata.status+').');
    const body=await metadata.json() as Record<string,unknown>;
    if(body.issuer!==issuer||body.authorization_endpoint!==issuer+'/authorize'||
        body.token_endpoint!==issuer+'/oauth/token'||
        !Array.isArray(body.code_challenge_methods_supported)||
        !body.code_challenge_methods_supported.includes('S256'))
        throw Error('OAuth Worker published but protocol discovery is incompatible.');
    console.log('Protected Weave OAuth issuer published and verified; demo resource binding remains unchanged.');
}

if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try{
        if(process.argv.length!==2)throw Error('No command-line arguments accepted.');
        await deployApprovedIssuer();
    }catch(error){
        // Only our own fixed, nonsecret error categories may be emitted.
        console.error(error instanceof Error?error.message:'Protected OAuth rollout failed.');
        process.exitCode=1;
    }
}
