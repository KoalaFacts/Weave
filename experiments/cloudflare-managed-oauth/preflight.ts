import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

type Json = Record<string, unknown>;
type Environment = Record<string, string | undefined>;
type TrialApplication = {id:string;managedOAuth:boolean;matchesTrialName:boolean};
const host = 'demo.weave-389.workers.dev';
const trialName = 'Weave Managed OAuth Trial';
const uuid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const object = (value:unknown):Json => value && typeof value === 'object' && !Array.isArray(value) ? value as Json : {};

/** Read-only operator preflight. A readable API does NOT prove write permission or a successful login. */
export async function inspectAccess({env = process.env,fetchImpl = fetch}: {
    env?:Environment;fetchImpl?:typeof fetch
} = {}) {
    const account = env.CLOUDFLARE_ACCOUNT_ID ?? '';
    const workerToken = env.CLOUDFLARE_API_TOKEN ?? '';
    const accessToken = env.CLOUDFLARE_ACCESS_TRIAL_TOKEN || workerToken;
    if(!/^[0-9a-f]{32}$/i.test(account) || !workerToken || !accessToken)
        throw Error('Cloudflare account and credential references are required.');
    const api = 'https://api.cloudflare.com/client/v4/accounts/' + account;
    async function get(path:string,token:string,label:string):Promise<{status:number;data:Json|null}> {
        let response:Response;
        try {
            response = await fetchImpl(api + path,{
                method:'GET',redirect:'manual',signal:AbortSignal.timeout(10000),
                headers:{Authorization:'Bearer ' + token,Accept:'application/json'}
            });
        } catch { throw Error(label + ' transport failed; no retry or mutation.'); }
        if(!response.ok) {
            await response.body?.cancel().catch(() => {});
            return {status:response.status,data:null};
        }
        const reader = response.body?.getReader();
        if(!reader) throw Error(label + ' metadata read failed.');
        let data:Json;
        try {
            let size=0;
            const chunks:Uint8Array[]=[];
            for(;;) {
                const {done,value}=await reader.read();
                if(done) break;
                size+=value.length;
                if(size>262144) throw Error('limit');
                chunks.push(value);
            }
            data=object(JSON.parse(Buffer.concat(chunks).toString('utf8')));
            if(data.success!==true || !('result' in data)) throw Error('envelope');
        } catch { throw Error(label + ' metadata read failed.'); }
        finally { await reader.cancel().catch(() => {});reader.releaseLock(); }
        return {status:response.status,data};
    }
    const identity=await get('/workers/subdomain',workerToken,'Account');
    if(identity.status!==200 || object(identity.data?.result).subdomain!=='weave-389')
        throw Error('Unable to verify the authorized Weave account.');
    const apps:{status:number;complete:boolean;trial:TrialApplication[]}={status:0,complete:false,trial:[]};
    for(let page=1;page<=10;page++) {
        const response=await get('/access/apps?per_page=100&page='+page,accessToken,'Access applications');
        apps.status=response.status;
        if(!response.data) break;
        if(!Array.isArray(response.data.result)) throw Error('Access application metadata is invalid.');
        const pagination=object(response.data.result_info);
        const total=pagination.total_pages;
        if(pagination.page!==page || typeof total!=='number' || !Number.isSafeInteger(total) || total<page || total>10)
            throw Error('Access application pagination is incomplete or exceeds the bounded limit.');
        for(const value of response.data.result) {
            const app=object(value);
            // Cloudflare destinations supersede legacy self_hosted_domains.
            const additional=Array.isArray(app.destinations)
                ? app.destinations.filter(value=>object(value).type==='public' || object(value).type===undefined).map(value=>object(value).uri)
                : Array.isArray(app.self_hosted_domains)?app.self_hosted_domains:[];
            const isHost=[app.domain,...additional].some(value=>{
                if(typeof value!=='string') return false;
                const hostname=value.split('/')[0].toLowerCase();
                return hostname===host || hostname==='*.weave-389.workers.dev';
            });
            if(app.name!==trialName && !isHost) continue;
            if(typeof app.id!=='string' || !uuid.test(app.id)) throw Error('Access application metadata is invalid.');
            apps.trial.push({id:app.id,managedOAuth:object(app.oauth_configuration).enabled===true,matchesTrialName:app.name===trialName});
        }
        if(page===total){apps.complete=true;break;}
    }
    const organizationResponse=await get('/access/organizations',accessToken,'Access organization');
    const organization:{status:number;configured:boolean;authDomain:string|null}={status:organizationResponse.status,configured:false,authDomain:null};
    if(organizationResponse.data) {
        const domain=object(organizationResponse.data.result).auth_domain;
        if(typeof domain!=='string' || !/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.cloudflareaccess\.com$/.test(domain))
            throw Error('Access organization metadata is invalid.');
        organization.configured=true;organization.authDomain=domain;
    }
    const report={
        outcome:apps.complete && organization.configured?'readable':'blocked',
        apps,organization,writePermissionVerified:false,readOnly:true,
        note:'GET-only account preflight. No application, policy, secret, Worker, database or subscription changes. HTTP status does not establish write permission or authentication success.'
    };
    const serialized=JSON.stringify(report);
    if([account,workerToken,accessToken].some(value=>serialized.includes(value)))
        throw Error('Credential reflection prevented preflight output.');
    return report;
}

if(process.argv[1] && import.meta.url===pathToFileURL(resolve(process.argv[1])).href) {
    try {
        if(process.argv.length!==2) throw Error('Access preflight accepts no arguments.');
        const report=await inspectAccess();
        console.log(JSON.stringify(report,null,2));
        if(report.outcome!=='readable') process.exitCode=2;
    } catch {
        // Never echo transport errors, response bodies or environment values.
        console.error('Access preflight failed safely; no cloud changes were attempted.');
        process.exitCode=1;
    }
}
