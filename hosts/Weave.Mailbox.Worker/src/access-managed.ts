/** Cloudflare Access Managed OAuth origin boundary. Cloudflare's OAuth bearer is
 * opaque: only the separately signed origin assertion is proof of the subject.
 * This function returns NO Weave authority. A first-party approved account link
 * must be resolved separately on every protected operation.
 */
export interface ManagedAccessEnvironment {
    ACCESS_TRIAL_ENABLED?: string;
    ACCESS_TRIAL_ISSUER?: string;
    ACCESS_TRIAL_RESOURCE?: string;
    ACCESS_TRIAL_AUDIENCE?: string;
}
export interface ManagedAccessConfiguration { issuer:string;resource:string;audience:string; }
export interface ManagedAccessSubject { issuer:string;resource:string;subject:string;expiresAt:number; }
const safeToken = /^[A-Za-z0-9_-]+$/;
const object = (value:unknown):value is Record<string,unknown> => value!==null && typeof value==='object' && !Array.isArray(value);
export function managedAccessConfiguration(env:ManagedAccessEnvironment,requestUrl:string):ManagedAccessConfiguration|null {
    if(env.ACCESS_TRIAL_ENABLED===undefined&&env.ACCESS_TRIAL_ISSUER===undefined&&env.ACCESS_TRIAL_RESOURCE===undefined&&env.ACCESS_TRIAL_AUDIENCE===undefined)return null;
    if(env.ACCESS_TRIAL_ENABLED!=='true'||!env.ACCESS_TRIAL_ISSUER||!env.ACCESS_TRIAL_RESOURCE||!env.ACCESS_TRIAL_AUDIENCE)throw Error('Incomplete Access Managed OAuth trial configuration');
    const issuer=new URL(env.ACCESS_TRIAL_ISSUER),resource=new URL(env.ACCESS_TRIAL_RESOURCE),incoming=new URL(requestUrl);
    if(issuer.protocol!=='https:'||!/^[-a-z0-9]+\.cloudflareaccess\.com$/.test(issuer.hostname)||issuer.username||issuer.password||issuer.port||issuer.pathname!=='/'||issuer.search||issuer.hash||issuer.origin!==env.ACCESS_TRIAL_ISSUER||
        resource.protocol!=='https:'||resource.pathname!=='/access-trial/mcp'||resource.search||resource.hash||resource.username||resource.password||resource.href!==env.ACCESS_TRIAL_RESOURCE||
        resource.origin!==incoming.origin||!safeToken.test(env.ACCESS_TRIAL_AUDIENCE)||env.ACCESS_TRIAL_AUDIENCE.length<16||env.ACCESS_TRIAL_AUDIENCE.length>128)
        throw Error('Invalid Access Managed OAuth trial binding');
    return {issuer:env.ACCESS_TRIAL_ISSUER,resource:env.ACCESS_TRIAL_RESOURCE,audience:env.ACCESS_TRIAL_AUDIENCE};
}
const decode=(raw:string):Uint8Array|null=>{
    if(!raw||raw.length>8192||!safeToken.test(raw))return null;
    try {const binary=atob(raw.replace(/-/g,'+').replace(/_/g,'/'));
        const bytes=Uint8Array.from(binary,char=>char.charCodeAt(0));
        if(btoa(binary).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'')!==raw)return null;
        return bytes;
    }catch{return null;}
};
async function safeKeys(config:ManagedAccessConfiguration,fetcher:typeof fetch):Promise<unknown[]|null> {
    try {
        // Never discover remote JWKS from untrusted JWT headers, claims or redirects.
        const result=await fetcher(config.issuer+'/cdn-cgi/access/certs',{
            method:'GET',redirect:'manual',cache:'no-store',signal:AbortSignal.timeout(3500),
            headers:{Accept:'application/json'}});
        if(!result.ok||!/^application\/json(?:;|$)/i.test(result.headers.get('content-type')??'')){
            await result.body?.cancel();return null;
        }
        const reader=result.body?.getReader();if(!reader)return null;
        const decoder=new TextDecoder('utf-8',{fatal:true});let size=0,plain='';
        try{while(true){const next=await reader.read();if(next.done)break;
            size+=next.value.byteLength;if(size>65536)return null;
            plain+=decoder.decode(next.value,{stream:true});}
            plain+=decoder.decode();
        }finally{await reader.cancel().catch(()=>{});reader.releaseLock();}
        const data:unknown=JSON.parse(plain);
        if(!object(data)||!Array.isArray(data.keys)||data.keys.length>12)return null;
        return data.keys;
    }catch{return null;}
}
/** Enforces RS256 signature, issuer, application AUD, expiry and subject.
 * HTTP headers are untrusted regardless of where a Worker can be reached.
 * Successful verification alone never registers, links or authorizes a Weave account.
 */
export async function verifyManagedAccessAssertion(
    cfg:ManagedAccessConfiguration,request:Request,fetcher:typeof fetch=fetch,now:number|(()=>number)=Date.now
):Promise<ManagedAccessSubject|null> {
    const jwt=request.headers.get('Cf-Access-Jwt-Assertion');
    if(!jwt||jwt.length>8192||!/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(jwt))return null;
    const clock=typeof now==='function'?now:()=>now;
    const [rawHeader,rawPayload,rawSignature]=jwt.split('.');
    const headerBytes=decode(rawHeader),payloadBytes=decode(rawPayload),sig=decode(rawSignature);
    if(!headerBytes||!payloadBytes||!sig)return null;
    try {
        const decoder=new TextDecoder('utf-8',{fatal:true});
        const header:unknown=JSON.parse(decoder.decode(headerBytes));
        const claims:unknown=JSON.parse(decoder.decode(payloadBytes));
        if(!object(header)||header.alg!=='RS256'||typeof header.kid!=='string'||header.kid.length>128||!safeToken.test(header.kid)||
            (header.typ!==undefined&&header.typ!=='JWT')||!object(claims))return null;
        const sec=Math.floor(clock()/1000);
        if(claims.iss!==cfg.issuer||!((Array.isArray(claims.aud)&&claims.aud.includes(cfg.audience))||claims.aud===cfg.audience)||
            typeof claims.sub!=='string'||!claims.sub||claims.sub.length>255||/[\x00-\x1f\x7f]/.test(claims.sub)||
            !Number.isSafeInteger(claims.exp)||!Number.isSafeInteger(claims.iat)||
            (claims.exp as number)<=sec||(claims.exp as number)>sec+28800||
            (claims.iat as number)>sec+60||(claims.iat as number)>=(claims.exp as number)||
            (claims.exp as number)-(claims.iat as number)>28800||
            (claims.nbf!==undefined&&(!Number.isSafeInteger(claims.nbf)||(claims.nbf as number)>sec)))return null;
        const keys=await safeKeys(cfg,fetcher);if(!keys)return null;
        const matches=keys.filter(k=>object(k)&&k.kid===header.kid&&k.kty==='RSA'&&
            (k.alg===undefined||k.alg==='RS256')&&(k.use===undefined||k.use==='sig')&&
            (k.key_ops===undefined||(Array.isArray(k.key_ops)&&k.key_ops.includes('verify')))&&
            typeof k.n==='string'&&typeof k.e==='string');
        if(matches.length!==1)return null;
        const key=await crypto.subtle.importKey('jwk',matches[0] as JsonWebKey,
            {name:'RSASSA-PKCS1-v1_5',hash:'SHA-256'},false,['verify']);
        // TS7 DOM BufferSource requires ArrayBuffer-backed bytes, not an optional SharedArrayBuffer.
        const signature=new Uint8Array(sig.byteLength);signature.set(sig);
        const valid=await crypto.subtle.verify('RSASSA-PKCS1-v1_5',key,signature,new TextEncoder().encode(rawHeader+'.'+rawPayload));
        if(!valid||(claims.exp as number)<=Math.floor(clock()/1000))return null;
        return {issuer:cfg.issuer,resource:cfg.resource,subject:claims.sub,expiresAt:claims.exp as number};
    }catch{return null;}
}
