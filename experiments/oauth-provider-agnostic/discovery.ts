/** Read-only protocol experiment. No client registration, credentials or cloud writes. */
export interface DiscoveryReport {
    outcome:'resource-metadata-absent'|'resource-metadata-unavailable'|'issuer-approval-required'|
        'metadata-rejected'|'issuer-metadata-unavailable'|'metadata-compatible';
    authenticated:false;
    resourceMetadataStatus?:number;
    issuerMetadataStatus?:number;
    authorizationCode?:boolean;
    pkceS256?:boolean;
    authorizationResponseIssuer?:boolean;
}
function endpoint(raw:string,allowQuery=false):URL {
    const url=new URL(raw);
    if(url.protocol!=='https:'||url.username||url.password||(!allowQuery&&url.search)||url.hash)throw Error('Expected a credential-free HTTPS target');
    return url;
}
const object=(value:unknown):value is Record<string,unknown>=>!!value&&typeof value==='object'&&!Array.isArray(value);
async function metadata(url:URL,fetcher:typeof fetch):Promise<{status?:number;data?:Record<string,unknown>;challenge?:string}> {
    let response:Response|undefined;
    try {
        response=await fetcher(url.href,{method:'GET',redirect:'error',credentials:'omit',cache:'no-store',
            signal:AbortSignal.timeout(10000),headers:{Accept:'application/json'}});
        if(!response.ok||!/^application\/json(?:\s*;|$)/i.test(response.headers.get('content-type')??'')){
            await response.body?.cancel();return {status:response.status,challenge:response.headers.get('WWW-Authenticate')??undefined};
        }
        const reader=response.body?.getReader();if(!reader)return {status:response.status};
        const decoder=new TextDecoder('utf-8',{fatal:true});let text='',count=0;
        try {
            while(true){const part=await reader.read();if(part.done)break;
                count+=part.value.byteLength;if(count>8192)return {status:response.status};
                text+=decoder.decode(part.value,{stream:true});}
            text+=decoder.decode();
        }finally{await reader.cancel().catch(()=>{});reader.releaseLock();}
        const data:unknown=JSON.parse(text);return {status:response.status,...(object(data)?{data}:{})};
    }catch{return {status:response?.status};}
}
/** RFC 9728 allows a 401 challenge to name a metadata URL. This read-only
 * diagnostic follows only an HTTPS same-origin well-known path, never a bearer,
 * redirect, cookie, cross-origin URL or arbitrary resource address.
 */
function challengeMetadata(header:string|undefined,resource:URL):URL|undefined {
    if(!header||header.length>2048||!/^Bearer\s/i.test(header))return;
    const matches=[...header.matchAll(/(?:^Bearer\s+|,)\s*resource_metadata\s*=\s*"([^"\r\n]{1,1024})"/gi)];
    if(matches.length!==1)return;
    try {
        const result=endpoint(matches[0][1]);
        if(result.origin!==resource.origin||!result.pathname.startsWith('/.well-known/')||
            result.href!==matches[0][1])return;
        return result;
    }catch{return;}
}
/** Only fetch the AS when the caller supplied its exact expected issuer.
 * No URL, claim, token, exception text or untrusted response body is logged.
 */
export async function inspectOAuthDiscovery(resource:string,expectedIssuer?:string,fetcher:typeof fetch=fetch):Promise<DiscoveryReport> {
    const target=endpoint(resource);
    const trusted=expectedIssuer===undefined?undefined:endpoint(expectedIssuer);
    const first=new URL('/.well-known/oauth-protected-resource'+target.pathname.replace(/\/$/,''),target);
    let found=await metadata(first,fetcher);
    if((found.status===404||found.status===410)&&target.pathname!=='/')
        found=await metadata(new URL('/.well-known/oauth-protected-resource',target),fetcher);
    if(found.status===401){
        let location=challengeMetadata(found.challenge,target);
        if(!location){
            // A gateway may challenge the resource but block its well-known paths.
            // Never attempt a write, send credentials, or follow arbitrary origins.
            const response=await metadata(target,fetcher);
            if(response.status===401)location=challengeMetadata(response.challenge,target);
        }
        if(location)found=await metadata(location,fetcher);
    }
    const report:DiscoveryReport={outcome:'resource-metadata-unavailable',authenticated:false,resourceMetadataStatus:found.status};
    if(found.status===404||found.status===410)return {...report,outcome:'resource-metadata-absent'};
    if(!found.data)return report;
    const value=found.data;
    if(value.resource!==target.href||!Array.isArray(value.authorization_servers)||
        value.authorization_servers.length===0||!value.authorization_servers.every(item=>typeof item==='string'))
        return {...report,outcome:'metadata-rejected'};
    if(!trusted||expectedIssuer===undefined)return {...report,outcome:'issuer-approval-required'};
    if(!value.authorization_servers.includes(expectedIssuer))return {...report,outcome:'metadata-rejected'};
    const issuerPath=trusted.pathname.replace(/\/$/,'');
    const locations=['/.well-known/oauth-authorization-server'+issuerPath,'/.well-known/openid-configuration'+issuerPath,
        ...(issuerPath?[issuerPath+'/.well-known/openid-configuration']:[])];
    let as:Awaited<ReturnType<typeof metadata>>={};
    for(const path of locations){
        as=await metadata(new URL(path,trusted),fetcher);
        if(as.status!==404&&as.status!==410)break;
    }
    report.issuerMetadataStatus=as.status;
    if(!as.data)return {...report,outcome:'issuer-metadata-unavailable'};
    const config=as.data;
    const authorizationCode=Array.isArray(config.response_types_supported)&&config.response_types_supported.includes('code')&&
        (config.grant_types_supported===undefined||(Array.isArray(config.grant_types_supported)&&config.grant_types_supported.includes('authorization_code')));
    const pkceS256=Array.isArray(config.code_challenge_methods_supported)&&config.code_challenge_methods_supported.includes('S256');
    // Metadata endpoints are checked, never called by this anonymous probe.
    let urlsValid=false;
    try{urlsValid=typeof config.authorization_endpoint==='string'&&typeof config.token_endpoint==='string'&&
        !!endpoint(config.authorization_endpoint,true)&&!!endpoint(config.token_endpoint,true);}catch{urlsValid=false;}
    return {...report,outcome:config.issuer===expectedIssuer&&authorizationCode&&pkceS256&&urlsValid?'metadata-compatible':'metadata-rejected',
        authorizationCode,pkceS256,authorizationResponseIssuer:config.authorization_response_iss_parameter_supported===true};
}
