/** One-time, no-retry Cloudflare Access Managed OAuth trial creation.
 * Never reconfigure an existing Access application or write D1/Worker settings.
 * Both exact identities and a dedicated Access management token are mandatory.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';

type Env=Record<string,string|undefined>;
type Json=Record<string,unknown>;
type Preflight={
    outcome:string;
    apps:{complete:boolean;trial:unknown[]};
    organization:{configured:boolean;authDomain:string|null};
};
type Inspect=(options:{env:Env;fetchImpl:typeof fetch})=>Promise<Preflight>;
const appName='Weave Managed OAuth Trial';
const policyName='Weave trial exact identities';
const trialPath='demo.weave-389.workers.dev/access-trial/*';
const apiRoot='https://api.cloudflare.com/client/v4/accounts/';
const uuid=/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const mailbox=/^[a-z0-9][a-z0-9.!#$%&'*+/=?^_{|}~-]*@[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/;
const object=(value:unknown):Json|null=>value!==null&&typeof value==='object'&&!Array.isArray(value)?value as Json:null;

function validateInputs(env:Env):string[] {
    if(!/^[a-f0-9]{32}$/i.test(env.CLOUDFLARE_ACCOUNT_ID??'')||
        !env.CLOUDFLARE_API_TOKEN||!env.CLOUDFLARE_ACCESS_TRIAL_TOKEN)
        throw Error('Dedicated Cloudflare Access token and account credentials are required.');
    if(env.GITHUB_REPOSITORY!=='KoalaFacts/Weave'||env.GITHUB_REF!=='refs/heads/main'||
        env.GITHUB_EVENT_NAME!=='workflow_dispatch'||
        env.WEAVE_ACCESS_TRIAL_COMMIT!=='create-weave-access-trial')
        throw Error('Only an explicit protected main workflow dispatch may create this trial.');
    const raw=env.WEAVE_ACCESS_TRIAL_EMAILS??'';
    if(!raw||raw.length>2600||!raw.includes(','))throw Error('Two explicitly selected trial identities are required.');
    const emails=raw.split(',').map(value=>value.trim().toLowerCase());
    if(emails.length<2||emails.length>10||new Set(emails).size!==emails.length||
        emails.some(email=>email.length>254||!mailbox.test(email)||/[?*]/.test(email)))
        throw Error('Provide two to ten exact distinct email identities; domains and Everyone are disallowed.');
    return emails.sort();
}

async function checkedJson(fetchImpl:typeof fetch,url:string,token:string,label:string,method:'POST'|'GET',payload?:unknown):Promise<Json> {
    let response:Response;
    try {
        response=await fetchImpl(url,{
            method,redirect:'manual',signal:AbortSignal.timeout(10000),
            headers:{Authorization:'Bearer '+token,Accept:'application/json',
                ...(method==='POST'?{'Content-Type':'application/json'}:{})},
            ...(method==='POST'?{body:JSON.stringify(payload)}:{})
        });
    }catch{throw Error(label+' transport failed. No automatic retry.');}
    if(!([200,201].includes(response.status))) {
        await response.body?.cancel().catch(()=>{});
        throw Error(label+' HTTP '+response.status+'. No automatic retry.');
    }
    const reader=response.body?.getReader();
    if(!reader)throw Error(label+' response missing. No automatic retry.');
    try{
        const decoder=new TextDecoder('utf-8',{fatal:true});let raw='',size=0;
        for(;;){const chunk=await reader.read();if(chunk.done)break;
            size+=chunk.value.length;if(size>131072)throw Error('response too large');
            raw+=decoder.decode(chunk.value,{stream:true});}
        raw+=decoder.decode();
        const envelope=object(JSON.parse(raw));
        if(!envelope||envelope.success!==true||!('result'in envelope))throw Error('unexpected envelope');
        return envelope;
    }catch{throw Error(label+' response invalid. No automatic retry.');}
    finally{await reader.cancel().catch(()=>{});reader.releaseLock();}
}

/** Execute one real Access-only create after an independent read-only account preflight.
 * If the POST succeeds but verification fails, manually inspect the created app;
 * never run this function again assuming a failed POST did not take effect.
 */
export async function provisionTrial({env=process.env,fetchImpl=fetch,inspect}: {
    env?:Env;fetchImpl?:typeof fetch;inspect?:Inspect;
}={}) {
    const emails=validateInputs(env); // No network before explicit identity and permission gates.
    const read=inspect??(await import('./preflight.ts')).inspectAccess as Inspect;
    const previous=await read({env,fetchImpl});
    if(previous.outcome!=='readable'||previous.apps.complete!==true||
        previous.organization.configured!==true||
        !/^[a-z0-9-]+\.cloudflareaccess\.com$/.test(previous.organization.authDomain??''))
        throw Error('Access organization or application inventory is unreadable; no creation attempted.');
    if(previous.apps.trial.length!==0)
        throw Error('An existing application overlaps Weave or the trial name; no existing application will be changed.');
    const body={
        name:appName,type:'self_hosted',domain:trialPath,
        destinations:[{type:'public',uri:trialPath}],app_launcher_visible:false,
        oauth_configuration:{enabled:true,
            dynamic_client_registration:{enabled:true,allow_any_on_localhost:true,
                allow_any_on_loopback:true,allowed_uris:[] as string[]},
            grant:{access_token_lifetime:'15m',session_duration:'24h'}},
        policies:[{name:policyName,decision:'allow',
            include:emails.map(email=>({email:{email}}))}]
    };
    const base=apiRoot+env.CLOUDFLARE_ACCOUNT_ID+'/access/apps';
    const token=env.CLOUDFLARE_ACCESS_TRIAL_TOKEN!;
    const created=await checkedJson(fetchImpl,base,token,'Access trial creation','POST',body);
    const createdObject=object(created.result);
    const id=createdObject?.id;
    if(typeof id!=='string'||!uuid.test(id)||createdObject?.name!==appName)
        throw Error('Access trial creation response could not be verified; inspect application list before retry.');
    // GET only the newly created UUID. Never PUT an existing application or policy.
    const app=object((await checkedJson(fetchImpl,base+'/'+id,token,'Access trial verification','GET')).result);
    const destinations=app?.destinations;
    const destination=Array.isArray(destinations)?object(destinations[0]):null;
    const oauth=object(app?.oauth_configuration),dcr=object(oauth?.dynamic_client_registration),grant=object(oauth?.grant);
    if(app?.id!==id||app?.name!==appName||app?.type!=='self_hosted'||
        app?.domain!==trialPath||app?.app_launcher_visible===true||
        !Array.isArray(destinations)||destinations.length!==1||
        destination?.type!=='public'||destination?.uri!==trialPath||
        (destination?.overrides!==undefined&&(!Array.isArray(destination.overrides)||destination.overrides.length!==0))||
        oauth?.enabled!==true||dcr?.enabled!==true||dcr?.allow_any_on_localhost!==true||
        dcr?.allow_any_on_loopback!==true||!Array.isArray(dcr?.allowed_uris)||dcr.allowed_uris.length!==0||
        grant?.access_token_lifetime!=='15m'||grant?.session_duration!=='24h')
        throw Error('Access trial application verification failed after creation; inspect application before retry.');
    const policies=(await checkedJson(fetchImpl,base+'/'+id+'/policies',token,'Access trial policy verification','GET')).result;
    if(!Array.isArray(policies)||policies.length!==1)throw Error('Access trial policy verification failed after creation.');
    const policy=object(policies[0]),include=policy?.include;
    const allowed=Array.isArray(include)?include.map(item=>{
        const rule=object(item);
        if(!rule||Object.keys(rule).join(',')!=='email')return null;
        const email=object(rule.email);
        return email&&Object.keys(email).join(',')==='email'&&typeof email.email==='string'
            ?email.email.toLowerCase():null;
    }).sort():[];
    if(policy?.name!==policyName||policy?.decision!=='allow'||
        (policy.require!==undefined&&(!Array.isArray(policy.require)||policy.require.length!==0))||
        (policy.exclude!==undefined&&(!Array.isArray(policy.exclude)||policy.exclude.length!==0))||
        allowed.length!==emails.length||allowed.some((email,i)=>email!==emails[i]))
        throw Error('Access trial policy verification failed after creation.');
    // No addresses, tokens or policy membership leaked to workflow logs.
    return {outcome:'created-verified',applicationId:id,path:trialPath,allowIdentityCount:emails.length,oauthEnabled:true};
}

if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    try {
        if(process.argv.length!==2)throw Error('No CLI arguments accepted.');
        console.log(JSON.stringify(await provisionTrial(),null,2));
    }catch{
        console.error('Access trial creation was blocked or its result is uncertain. Inspect the exact Weave application before retrying; no automatic retry or rollback was attempted.');
        process.exitCode=1;
    }
}
