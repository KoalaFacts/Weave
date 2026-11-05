/** Publish new code to the ALREADY RUNNING demo OAuth Worker. Its first publication, with its secrets and Durable
 * Object migration, is the protected issuer release; this changes code only. The configuration comes from the
 * running Worker's own settings, and secrets, KV data and consent nonces are untouched.
 */
import {writeFile} from 'node:fs/promises';
import {join, resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {demoManifest} from './prepare-demo.ts';
import {type Binding, validateIssuerReadback, verifyDiscovery, wrangler} from './deploy-demo.ts';

const root=resolve(import.meta.dirname,'..');
const hexId=/^[0-9a-f]{32}$/i;

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
    try{validateIssuerReadback(bindings,expected);}
    catch{throw Error('The running OAuth Worker lacks an expected binding or secret; no change made.');}
    if(!hexId.test(expected.WEAVE_AUTH_KV_NAMESPACE_ID))throw Error('The running OAuth Worker has an unexpected KV binding; no change made.');
    return {manifest:demoManifest(env.CLOUDFLARE_ACCOUNT_ID!,expected.WEAVE_AUTH_KV_NAMESPACE_ID,expected.GITHUB_CLIENT_ID),expected};
}

export async function updateDemoIssuer(env:NodeJS.ProcessEnv=process.env,request:typeof fetch=fetch){
    const {manifest,expected}=await preflightUpdate(env,request);
    const filename='wrangler.update.generated.jsonc';
    await writeFile(join(root,filename),JSON.stringify(manifest,null,2)+'\n',{flag:'wx',mode:0o600});
    await wrangler(['deploy','--config',filename,'--keep-vars'],env);
    // The published version must still carry every binding and secret, then answer discovery.
    try{validateIssuerReadback(await runningBindings(env,request),expected);}
    catch{throw Error('OAuth Worker code published but its settings readback no longer matches; review it before another release.');}
    await verifyDiscovery(request);
    console.log('Demo OAuth Worker code updated and verified; secrets and stored state unchanged.');
}

if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try{
        if(process.argv.length!==2)throw Error('No command-line arguments accepted.');
        await updateDemoIssuer();
    }catch(error){
        // Only our own fixed, nonsecret error messages are emitted.
        console.error(error instanceof Error?error.message:'OAuth Worker update failed.');
        process.exitCode=1;
    }
}
