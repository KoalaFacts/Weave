import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

type Json = Record<string, unknown>;
type Environment = Record<string, string | undefined>;
type TrialApplication = {id:string;managedOAuth:boolean;matchesTrialName:boolean};
const host = 'demo.weave-389.workers.dev';
const trialName = 'Weave Managed OAuth Trial';
const uuid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const object = (value:unknown):Json => value && typeof value === 'object' && !Array.isArray(value) ? value as Json : {};

/** Only Cloudflare's structured integer error IDs are public diagnostics.
 * Discard untrusted text, metadata, excessive bodies and malformed streams.
 */
async function numericErrorCodes(response:Response):Promise<number[]> {
    const reader=response.body?.getReader();
    if(!reader)return [];
    try {
        let size=0;
        const chunks:Uint8Array[]=[];
        for(;;) {
            const {done,value}=await reader.read();
            if(done)break;
            size+=value.length;
            if(size>8192)return [];
            chunks.push(value);
        }
        const data=object(JSON.parse(new TextDecoder('utf-8',{fatal:true})
            .decode(Buffer.concat(chunks))));
        if(!Array.isArray(data.errors))return [];
        const codes:number[]=[];
        for(const entry of data.errors.slice(0,16)) {
            const code=object(entry).code;
            if(typeof code==='number' && Number.isSafeInteger(code) &&
                code>0 && code<=999999 && !codes.includes(code))
                codes.push(code);
            if(codes.length===4)break;
        }
        return codes;
    } catch {
        return [];
    } finally {
        await reader.cancel().catch(()=>{});
        reader.releaseLock();
    }
}

/** Read-only operator preflight. A readable API does NOT prove write permission or a successful login. */
export async function inspectAccess({env = process.env,fetchImpl = fetch}: {
    env?:Environment;fetchImpl?:typeof fetch
} = {}) {
    const account = env.CLOUDFLARE_ACCOUNT_ID ?? '';
    const token = env.CLOUDFLARE_API_TOKEN ?? '';
    if(!/^[0-9a-f]{32}$/i.test(account) || !token)
        throw Error('Cloudflare account and credential references are required.');
    const api = 'https://api.cloudflare.com/client/v4/accounts/' + account;
    async function get(path:string,token:string,label:string):Promise<{status:number;data:Json|null;errorCodes?:number[]}> {
        let response:Response;
        try {
            response = await fetchImpl(api + path,{
                method:'GET',redirect:'manual',signal:AbortSignal.timeout(10000),
                headers:{Authorization:'Bearer ' + token,Accept:'application/json'}
            });
        } catch { throw Error(label + ' transport failed; no retry or mutation.'); }
        if(!response.ok) {
            let errorCodes:number[]=[];
            if(response.status>=400)
                errorCodes=await numericErrorCodes(response);
            else
                await response.body?.cancel().catch(()=>{});
            return {status:response.status,data:null,errorCodes};
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
    const identity=await get('/workers/subdomain',token,'Account');
    if(identity.status!==200 || object(identity.data?.result).subdomain!=='weave-389')
        throw Error('Unable to verify the authorized Weave account.');
    const apps:{status:number;complete:boolean;trial:TrialApplication[];errorCodes?:number[]}={status:0,complete:false,trial:[]};
    for(let page=1;page<=10;page++) {
        const response=await get('/access/apps?per_page=100&page='+page,token,'Access applications');
        apps.status=response.status;
        if(!response.data) {apps.errorCodes=response.errorCodes??[];break;}
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
                // Ignore the path: a more specific rule can replace MFA/device
                // requirements inherited from a broader host/path Access app.
                if(hostname===host || hostname==='*') return true;
                // Retain conservative parent wildcard coverage even when a
                // documented '*' covers fewer subdomain levels.
                if(hostname.startsWith('*.') && host.endsWith(hostname.slice(1)))
                    return true;
                if(!hostname.includes('*')) return false;
                // Cloudflare supports prefix/suffix wildcards inside one
                // subdomain label, e.g. dem*.weave-389.workers.dev. A wildcard
                // cannot consume a dot. If a label has multiple wildcards,
                // its actual scope is ambiguous: never silently override it.
                const pattern=hostname.split('.'),actual=host.split('.');
                if(pattern.length!==actual.length) return false;
                if(pattern.some(label=>label.indexOf('*')!==label.lastIndexOf('*')))
                    return true;
                return pattern.every((label,index)=>{
                    const star=label.indexOf('*');
                    if(star<0) return label===actual[index];
                    const prefix=label.slice(0,star),suffix=label.slice(star+1);
                    return actual[index].length>=prefix.length+suffix.length &&
                        actual[index].startsWith(prefix) &&
                        actual[index].endsWith(suffix);
                });
            });
            // Cloudflare gives hostname/path rules higher precedence than
            // Worker and account rules. Conservatively refuse to create a new
            // application when these existing destinations cannot be proved
            // disjoint; the read-only inventory does not grant override consent.
            const potentiallyWorkerWide=Array.isArray(app.destinations)&&
                app.destinations.some(value=>['all_workers','all_preview_workers',
                    'worker','preview_worker'].includes(String(object(value).type)));
            if(app.name!==trialName && !isHost && !potentiallyWorkerWide) continue;
            if(typeof app.id!=='string' || !uuid.test(app.id)) throw Error('Access application metadata is invalid.');
            apps.trial.push({id:app.id,managedOAuth:object(app.oauth_configuration).enabled===true,matchesTrialName:app.name===trialName});
        }
        if(page===total){apps.complete=true;break;}
    }
    const organizationResponse=await get('/access/organizations',token,'Access organization');
    const organization:{status:number;configured:boolean;authDomain:string|null;errorCodes?:number[]}={status:organizationResponse.status,configured:false,authDomain:null};
    if(!organizationResponse.data)organization.errorCodes=organizationResponse.errorCodes??[];
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
    if([account,token].some(value=>serialized.includes(value)))
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
