/** Interactive OAuth authorization-code + PKCE S256 for a public MCP client.
 * Tokens remain in caller memory; this is not an authorization server or vault.
 */
import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { createServer } from 'node:http';

/** The canonical demo can use public CIMD without enabling anonymous DCR.
 * The authorization server must explicitly advertise CIMD support first.
 */
export const demoOAuthClientId = 'https://demo.weave-389.workers.dev/oauth-client-metadata.json';
export function selectOAuthClientId(resource:string,configuredId:string,cimdSupported:unknown):string{
    if(configuredId)return configuredId;
    return resource==='https://demo.weave-389.workers.dev/mcp' && cimdSupported===true
        ?demoOAuthClientId:'';
}
const allowedScopes=new Set(['weave:addresses.read','weave:addresses.write','weave:contacts.read','weave:contacts.write',
    'weave:messages.read','weave:messages.send','weave:messages.ack']);
const loopback=(url:URL)=>url.protocol==='http:' && ['127.0.0.1','[::1]','localhost'].includes(url.hostname);
const validEndpoint=(raw:string,allowLoopback:boolean):URL=>{
    const url=new URL(raw);
    if((url.protocol!=='https:'&&!(allowLoopback&&loopback(url)))||!url.hostname||url.username||url.password||url.hash)
        throw Error('OAuth requires HTTPS endpoints (except explicit local loopback tests)');
    return url;
};
class OAuthDiscoveryNotFound extends Error {}
async function boundedJson(url:URL,init:RequestInit|undefined,allowLoopback:boolean):Promise<Record<string,unknown>>{
    validEndpoint(url.href,allowLoopback);
    let response:Response;
    try{response=await fetch(url,{...init,redirect:'error',cache:'no-store',signal:AbortSignal.timeout(10000)});}
    catch{throw Error('OAuth metadata or token endpoint unavailable');}
    if(!response.ok||!/^application\/json(?:\s*;|$)/i.test(response.headers.get('content-type')??'')){
        const missing=response.status===404||response.status===410;
        await response.body?.cancel();
        if(missing)throw new OAuthDiscoveryNotFound();
        throw Error('OAuth metadata or token response unavailable');
    }
    const reader=response.body?.getReader();if(!reader)throw Error('OAuth returned no response');
    let bytes=0,text='';const decoder=new TextDecoder('utf-8',{fatal:true});
    try{
        while(true){const item=await reader.read();if(item.done)break;
            bytes+=item.value.byteLength;if(bytes>8192)throw Error('OAuth response exceeded size limit');
            text+=decoder.decode(item.value,{stream:true});
        }
        text+=decoder.decode();
    }finally{await reader.cancel().catch(()=>{});reader.releaseLock();}
    let data:unknown;try{data=JSON.parse(text);}catch{throw Error('Invalid OAuth metadata or token response');}
    if(!data||typeof data!=='object'||Array.isArray(data))throw Error('Invalid OAuth metadata or token response');
    return data as Record<string,unknown>;
}
function sameIssuerUrl(raw:unknown,issuer:URL,allowLoopback:boolean,trustedOrigins:ReadonlySet<string>):URL{
    if(typeof raw!=='string')throw Error('Missing OAuth authorization or token endpoint');
    const url=validEndpoint(raw,allowLoopback);
    if(url.origin!==issuer.origin&&!trustedOrigins.has(url.origin))throw Error('OAuth endpoint is outside trusted issuer origin');
    return url;
}
export interface OAuthLoginOptions {
    /** Reserved for local synthetic HTTP tests; never allows arbitrary HTTP origins. */
    allowLoopbackForTests?:boolean;
    scopes?:string[];
    timeoutMs?:number;
    /** Optional operator-approved cross-origin RFC 8414 endpoints (exact scheme/host/port). */
    trustedEndpointOrigins?:readonly string[];
    /** Exact operator-approved issuer when the protected resource advertises multiple issuers. */
    expectedIssuer?:string;
}
/** onAuthorizationUrl opens the trusted AS consent page (or shows the link to
 * the operator). It must never receive the verifier, access token, or code.
 */
export async function authorizeMcpAccount(resource:string,clientId:string,
    onAuthorizationUrl:(url:string)=>Promise<void>|void,options:OAuthLoginOptions={}):Promise<string>{
    const allowLoopback=options.allowLoopbackForTests===true;
    const trustedOrigins=new Set<string>();
    for(const origin of options.trustedEndpointOrigins??[]){
        if(typeof origin!=='string')throw Error('Invalid trusted OAuth endpoint origin');
        const url=validEndpoint(origin,allowLoopback);
        if(url.href!==url.origin+'/'||url.pathname!=='/'||url.search||url.hash)
            throw Error('Trusted OAuth endpoint origin must be an exact origin');
        trustedOrigins.add(url.origin);
    }
    const endpoint=validEndpoint(resource,allowLoopback);
    if(endpoint.pathname!=='/mcp'||endpoint.search)throw Error('OAuth resource must be the exact /mcp URL');
    // An empty client ID requests RFC 7591 dynamic registration. Nonempty IDs are operator-provisioned.
    if(clientId&&!/^[\x21-\x7e]{1,128}$/.test(clientId))throw Error('Invalid public OAuth client ID');
    const scopes=options.scopes??[...allowedScopes];
    if(!scopes.length||scopes.some(x=>!allowedScopes.has(x))||new Set(scopes).size!==scopes.length)
        throw Error('Unsupported OAuth scope request');
    const protectedResource=new URL('/.well-known/oauth-protected-resource/mcp',endpoint);
    let protectedMetadata:Record<string,unknown>;
    try {protectedMetadata=await boundedJson(protectedResource,undefined,allowLoopback);}
    catch(error){
        // RFC 9728 also allows the root well-known location, but never
        // interpret a 401, redirect, 5xx or malformed response as absence.
        if(!(error instanceof OAuthDiscoveryNotFound))throw error;
        protectedMetadata=await boundedJson(new URL('/.well-known/oauth-protected-resource',endpoint),undefined,allowLoopback);
    }
    const advertised=protectedMetadata.authorization_servers;
    if(protectedMetadata.resource!==endpoint.href||!Array.isArray(advertised)||
        advertised.length<1||advertised.length>8||!advertised.every(value=>typeof value==='string'))
        throw Error('OAuth protected resource has no trustworthy issuer');
    // A multi-issuer resource requires a pinned operator selection. Never
    // choose an arbitrary first issuer from untrusted resource metadata.
    const selectedIssuer=options.expectedIssuer??(advertised.length===1?advertised[0]:null);
    if(typeof selectedIssuer!=='string'||!advertised.includes(selectedIssuer))
        throw Error('OAuth issuer requires explicit operator selection');
    const issuer=validEndpoint(selectedIssuer,allowLoopback);
    if(issuer.search||issuer.hash)throw Error('OAuth issuer must not include query parameters');
    // RFC 8414 first, then the two OIDC tenant discovery conventions.
    // Only a genuine 404/410 can trigger fallback; authentication errors,
    // redirects, invalid JSON and issuer mismatches must fail closed.
    const suffix=issuer.pathname.replace(/\/$/,'');
    const discoveryUrls=[
        new URL('/.well-known/oauth-authorization-server'+suffix,issuer),
        new URL('/.well-known/openid-configuration'+suffix,issuer),
        ...(suffix?[new URL(suffix+'/.well-known/openid-configuration',issuer)]:[])
    ];
    let metadata:Record<string,unknown>|undefined;
    for(const discovery of discoveryUrls){
        try{metadata=await boundedJson(discovery,undefined,allowLoopback);break;}
        catch(error){if(!(error instanceof OAuthDiscoveryNotFound))throw error;}
    }
    if(!metadata)throw Error('OAuth issuer does not expose RFC 8414 or OpenID Connect metadata');
    const responseIssuerFlag=metadata.authorization_response_iss_parameter_supported;
    if((responseIssuerFlag!==undefined&&typeof responseIssuerFlag!=='boolean')||
        metadata.issuer!==selectedIssuer||
        !Array.isArray(metadata.response_types_supported)||!metadata.response_types_supported.includes('code')||
        (metadata.grant_types_supported!==undefined&&(!Array.isArray(metadata.grant_types_supported)||!metadata.grant_types_supported.includes('authorization_code')))||
        !Array.isArray(metadata.code_challenge_methods_supported)||!metadata.code_challenge_methods_supported.includes('S256'))
        throw Error('Untrusted OAuth issuer or authorization-code PKCE S256 unsupported');
    const authorization=sameIssuerUrl(metadata.authorization_endpoint,issuer,allowLoopback,trustedOrigins);
    const tokenEndpoint=sameIssuerUrl(metadata.token_endpoint,issuer,allowLoopback,trustedOrigins);
    // A trusted first-party demo uses its HTTPS Client ID Metadata Document
    // only when the AS advertises CIMD. Never enable DCR merely for that client;
    // foreign resources and explicit operator IDs keep their existing semantics.
    const resolvedClientId=selectOAuthClientId(endpoint.href,clientId,metadata.client_id_metadata_document_supported);
    // Discover but never blindly contact an untrusted registration origin.
    const registrationEndpoint=resolvedClientId?null:
        (typeof metadata.registration_endpoint==='string'
            ?sameIssuerUrl(metadata.registration_endpoint,issuer,allowLoopback,trustedOrigins)
            :null);
    if(!resolvedClientId&&!registrationEndpoint)
        throw Error('OAuth issuer advertises neither trusted CIMD nor an approved registration endpoint');
    const verifier=randomBytes(32).toString('base64url');
    const challenge=createHash('sha256').update(verifier).digest('base64url');
    const state=randomBytes(32).toString('base64url');
    const timeout=options.timeoutMs??240000;
    if(!Number.isSafeInteger(timeout)||timeout<1||timeout>300000)throw Error('Invalid OAuth callback timeout');
    const server=createServer();
    await new Promise<void>((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
    try{
        const addr=server.address();if(!addr||typeof addr==='string')throw Error('OAuth loopback callback did not bind');
        const redirectUri=`http://127.0.0.1:${addr.port}/callback`;
        let selectedClientId=resolvedClientId;
        if(registrationEndpoint){
            // RFC 7591: register only this ephemeral loopback callback as a public
            // authorization-code/PKCE client. Never accept or persist a client secret.
            const registration=await boundedJson(registrationEndpoint,{
                method:'POST',headers:{'Content-Type':'application/json','Accept':'application/json'},
                body:JSON.stringify({client_name:'Weave remote MCP client',
                    redirect_uris:[redirectUri],token_endpoint_auth_method:'none',
                    grant_types:['authorization_code'],response_types:['code'],scope:scopes.join(' ')})
            },allowLoopback);
            if(typeof registration.client_id!=='string'||!/^[\x21-\x7e]{1,128}$/.test(registration.client_id)||
                registration.client_secret!==undefined||
                (registration.token_endpoint_auth_method!==undefined&&registration.token_endpoint_auth_method!=='none')||
                (registration.redirect_uris!==undefined&&
                    (!Array.isArray(registration.redirect_uris)||!registration.redirect_uris.includes(redirectUri))))
                throw Error('Invalid OAuth public client registration response');
            selectedClientId=registration.client_id;
        }
        const url=new URL(authorization);
        for(const [key,value] of Object.entries({response_type:'code',client_id:selectedClientId,redirect_uri:redirectUri,
            state,code_challenge:challenge,code_challenge_method:'S256',resource:endpoint.href,scope:scopes.join(' ')}))
            url.searchParams.set(key,value);
        let timer:ReturnType<typeof setTimeout>|undefined;
        let settled=false;
        const authorizationCode=new Promise<string>((resolve,reject)=>{
            const done=(code:string|Error)=>{
                if(settled)return;
                settled=true;if(timer)clearTimeout(timer);
                if(code instanceof Error)reject(code);else resolve(code);
            };
            server.on('request',(req,res)=>{
                const requestUrl=new URL(req.url??'/',redirectUri);
                res.setHeader('Cache-Control','no-store');res.setHeader('X-Content-Type-Options','nosniff');
                res.setHeader('Content-Security-Policy',"default-src 'none'");
                res.setHeader('Content-Type','text/plain; charset=utf-8');
                if(req.method!=='GET'||requestUrl.pathname!=='/callback'){
                    res.writeHead(404);res.end('Not found');return;
                }
                const supplied=requestUrl.searchParams.getAll('state');
                const suppliedBuffer=Buffer.from(supplied[0]??'');const stateBuffer=Buffer.from(state);
                if(supplied.length!==1||suppliedBuffer.length!==stateBuffer.length||
                    !timingSafeEqual(suppliedBuffer,stateBuffer)){
                    res.writeHead(400);res.end('Invalid OAuth state');done(Error('OAuth callback state mismatch'));return;
                }
                // RFC 9207: the returned issuer must match the trusted AS,
                // especially when the AS advertises issuer identification.
                const issuers=requestUrl.searchParams.getAll('iss');
                if(issuers.length>1||(issuers.length===0&&responseIssuerFlag===true)||
                    (issuers.length===1&&issuers[0]!==metadata.issuer)){
                    res.writeHead(400);res.end('Invalid OAuth issuer');done(Error('OAuth callback issuer mismatch'));return;
                }
                const code=requestUrl.searchParams.getAll('code');
                if(requestUrl.searchParams.has('error')||code.length!==1||!code[0]||code[0].length>4096){
                    res.writeHead(400);res.end('OAuth consent denied or invalid code');done(Error('OAuth callback rejected'));return;
                }
                res.writeHead(200);res.end('Authorization received. You may close this tab.');done(code[0]);
            });
            timer=setTimeout(()=>done(Error('OAuth callback timed out')),timeout);
            timer.unref?.();
        });
        void authorizationCode.catch(()=>{}); // Handle rejected callbacks even while opening the consent URL.
        try{
            await onAuthorizationUrl(url.href);
            const code=await authorizationCode;
            const form=new URLSearchParams({grant_type:'authorization_code',code,redirect_uri:redirectUri,
                client_id:selectedClientId,code_verifier:verifier,resource:endpoint.href});
            const token=await boundedJson(tokenEndpoint,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded',
                Accept:'application/json'},body:form},allowLoopback);
            if(typeof token.access_token!=='string'||!token.access_token||token.access_token.length>4096||
                (typeof token.token_type!=='string'||token.token_type.toLowerCase()!=='bearer')||!Number.isSafeInteger(token.expires_in)||
                (token.expires_in as number)<=0||(token.expires_in as number)>900)
                throw Error('Invalid OAuth token response');
            return token.access_token;
        }finally{if(timer)clearTimeout(timer);authorizationCode.catch(()=>{});}
    }finally{await new Promise<void>(resolve=>{server.close(()=>resolve());server.closeAllConnections();});}
}
