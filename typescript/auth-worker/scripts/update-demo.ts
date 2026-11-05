/** Publish new code to the ALREADY RUNNING demo OAuth Worker. Its first publication, with its secrets and Durable
 * Object migration, is the protected issuer release; this changes code only. The configuration comes from the
 * running Worker's own settings, and secrets, KV data and consent nonces are untouched.
 */
import {writeFile} from 'node:fs/promises';
import {join, resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {demoManifest} from './prepare-demo.ts';
import {type Binding, readbackMismatches, secretNames, verifyDiscovery, wrangler} from './deploy-demo.ts';

const root=resolve(import.meta.dirname,'..');
const hexId=/^[0-9a-f]{32}$/i;
/** Every binding the manifest recreates, plus the secrets a deploy keeps. */
const recreated=new Set(['OAUTH_ISSUER','OAUTH_RESOURCE','GITHUB_CLIENT_ID','OAUTH_KV','CONSENT_NONCES','MAILBOX',...secretNames]);
/** Optional bindings the code uses and the update carries over unchanged: the two registration budgets. */
const limiters=new Set(['REGISTRATION_LIMITER','REGISTRATION_GLOBAL_LIMITER']);
type RateLimit={name:string;namespace_id:string;simple:{limit:number;period:number}};

/** A registration budget exactly as configured, or null when it is not one this update can recreate. */
function rateLimit(binding:Binding):RateLimit|null{
    const {limit,period}=binding.simple??{};
    if(binding.type!=='ratelimit'||!/^[1-9][0-9]{0,9}$/.test(binding.namespace_id??'')||!Number.isSafeInteger(limit)||!Number.isSafeInteger(period))return null;
    return {name:binding.name,namespace_id:binding.namespace_id!,simple:{limit:limit as number,period:period as number}};
}
const sameLimits=(bindings:Binding[],expected:RateLimit[])=>expected.every(want=>bindings.some(b=>{
    const have=b.name===want.name?rateLimit(b):null;
    return have!==null&&JSON.stringify(have)===JSON.stringify(want);
}));

/** The running Worker's bindings; throws unless it exists with every binding and secret the code needs. */
async function runningBindings(env:NodeJS.ProcessEnv,request:typeof fetch):Promise<Binding[]>{
    const url='https://api.cloudflare.com/client/v4/accounts/'+env.CLOUDFLARE_ACCOUNT_ID+'/workers/scripts/weave-auth-demo/settings';
    let response:Response;
    try{response=await request(url,{method:'GET',redirect:'error',headers:{Authorization:'Bearer '+env.CLOUDFLARE_API_TOKEN},
        signal:AbortSignal.timeout(10000)});}catch{throw Error('OAuth Worker settings lookup unavailable; no change made.');}
    if(response.status===404)throw Error('The OAuth Worker is not published yet; its first release is the protected issuer workflow.');
    if(!response.ok)throw Error('OAuth Worker settings lookup denied (HTTP '+response.status+'); no change made.');
    const json=await response.json().catch(()=>null) as {success?:boolean;result?:{bindings?:Binding[]}}|null;
    if(json?.success!==true||!Array.isArray(json.result?.bindings))throw Error('OAuth Worker settings lookup was invalid; no change made.');
    return json.result.bindings;
}

/** The nonsecret configuration of the running Worker, rebuilt from its own bindings. Nothing is changed. */
export async function preflightUpdate(env:NodeJS.ProcessEnv,request:typeof fetch=fetch){
    if(!hexId.test(env.CLOUDFLARE_ACCOUNT_ID??'')||!env.CLOUDFLARE_API_TOKEN)throw Error('Existing Cloudflare account and token are required.');
    const bindings=await runningBindings(env,request);
    const clientId=bindings.find(b=>b.name==='GITHUB_CLIENT_ID')?.text;
    const kvId=bindings.find(b=>b.name==='OAUTH_KV')?.namespace_id;
    const expected={GITHUB_CLIENT_ID:typeof clientId==='string'?clientId:'',WEAVE_AUTH_KV_NAMESPACE_ID:kvId??''};
    const mismatched=readbackMismatches(bindings,expected);
    if(mismatched.length)throw Error('The running OAuth Worker lacks or differs in '+mismatched.join(', ')+'; no change made.');
    if(!hexId.test(expected.WEAVE_AUTH_KV_NAMESPACE_ID))throw Error('The running OAuth Worker has an unexpected KV binding; no change made.');
    // A code update must not drop configuration: each other binding is carried over exactly, or the update is refused.
    const ratelimits:RateLimit[]=[];
    for(const binding of bindings){
        if(recreated.has(binding.name))continue;
        const kept=limiters.has(binding.name)?rateLimit(binding):null;
        if(!kept)throw Error('The running OAuth Worker has a binding this update cannot preserve; no change made.');
        ratelimits.push(kept);
    }
    const manifest=demoManifest(env.CLOUDFLARE_ACCOUNT_ID!,expected.WEAVE_AUTH_KV_NAMESPACE_ID,expected.GITHUB_CLIENT_ID);
    return {manifest:ratelimits.length?{...manifest,ratelimits}:manifest,expected,ratelimits};
}

export async function updateDemoIssuer(env:NodeJS.ProcessEnv=process.env,request:typeof fetch=fetch){
    const {manifest,expected,ratelimits}=await preflightUpdate(env,request);
    const filename='wrangler.update.generated.jsonc';
    await writeFile(join(root,filename),JSON.stringify(manifest,null,2)+'\n',{flag:'wx',mode:0o600});
    await wrangler(['deploy','--config',filename,'--keep-vars'],env);
    // The published version must still carry every binding and secret, then answer discovery.
    const published=await runningBindings(env,request);
    const changed=[...readbackMismatches(published,expected),...(sameLimits(published,ratelimits)?[]:['registration budgets'])];
    if(changed.length)throw Error('OAuth Worker code published but its readback differs in '+changed.join(', ')+'; review it before another release.');
    await verifyDiscovery(request);
    console.log('Demo OAuth Worker code updated and verified; secrets and stored state unchanged.');
}

if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try{
        // --preflight only checks the running Worker, so a release can stop before it changes anything else.
        const args=process.argv.slice(2);
        if(args.length>1||(args.length===1&&args[0]!=='--preflight'))throw Error('Only --preflight is accepted.');
        if(args[0]==='--preflight'){
            await preflightUpdate(process.env);
            console.log('The running demo OAuth Worker has every binding and secret its code needs.');
        }else await updateDemoIssuer();
    }catch(error){
        // Only our own fixed, nonsecret error messages are emitted.
        console.error(error instanceof Error?error.message:'OAuth Worker update failed.');
        process.exitCode=1;
    }
}
