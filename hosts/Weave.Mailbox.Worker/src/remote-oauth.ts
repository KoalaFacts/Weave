/** OAuth 2 protected-resource boundary for individually authorized Weave accounts.
 * OAuth 2.1 code/PKCE/consent is performed by the configured authorization server,
 * never by a shared mailbox or by MCP tool arguments. Tokens are introspected on
 * every protected call so revocation is effective without a local token cache.
 */
export interface OAuthEnvironment {
    OAUTH_ISSUER?: string;
    OAUTH_RESOURCE?: string;
    OAUTH_INTROSPECTION_URL?: string;
    OAUTH_CLIENT_ID?: string;
    OAUTH_CLIENT_SECRET?: string;
}
export interface OAuthConfiguration {
    issuer: string;
    resource: string;
    introspectionUrl: string;
    clientId: string;
    clientSecret: string;
}
export type OAuthAccess = {accountId: string} | Response;
const identity = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const tokenPattern = /^[A-Za-z0-9\-._~+/]+={0,2}$/;
const scopes = ['weave:addresses.read','weave:addresses.write','weave:contacts.read','weave:contacts.write','weave:messages.read','weave:messages.send','weave:messages.ack'];
const noStore = {'Cache-Control':'no-store','Pragma':'no-cache','X-Content-Type-Options':'nosniff'};
const safeUrl = (raw: string, label: string): URL => {
    const parsed = new URL(raw);
    if (parsed.protocol !== 'https:' || parsed.username || parsed.password || parsed.search || parsed.hash || !parsed.hostname) throw Error('Invalid '+label);
    return parsed;
};
/** A partial/malformed deployment is *not* a fallback to anonymous private tools. */
export function oauthConfiguration(env: OAuthEnvironment, requestUrl: string): OAuthConfiguration | null {
    const values = [env.OAUTH_ISSUER,env.OAUTH_RESOURCE,env.OAUTH_INTROSPECTION_URL,env.OAUTH_CLIENT_ID,env.OAUTH_CLIENT_SECRET];
    if (values.every(v=>v===undefined)) return null;
    if (values.some(v=>!v)) throw Error('Incomplete OAuth configuration');
    const issuer=safeUrl(values[0]!, 'issuer'),resource=safeUrl(values[1]!, 'resource'),introspection=safeUrl(values[2]!, 'introspection');
    if (resource.pathname!=='/mcp'||resource.href!==values[1]||issuer.href.replace(/\/$/,'')!==values[0]!.replace(/\/$/,'')||introspection.origin!==issuer.origin) throw Error('OAuth binding mismatch');
    if (new URL(requestUrl).origin!==resource.origin) throw Error('Unexpected OAuth resource origin');
    if (!/^[\x21-\x7e]{1,128}$/.test(values[3]!)||values[3]!.includes(':')|| !/^[\x21-\x7e]{16,2048}$/.test(values[4]!)) throw Error('Invalid OAuth client credentials');
    return {issuer:values[0]!,resource:resource.href,introspectionUrl:introspection.href,clientId:values[3]!,clientSecret:values[4]!};
}
export function protectedResourceMetadata(config: OAuthConfiguration): Response {
    return new Response(JSON.stringify({resource:config.resource,authorization_servers:[config.issuer],bearer_methods_supported:['header'],scopes_supported:scopes}),
        {status:200,headers:{...noStore,'Content-Type':'application/json'}});
}
function challenge(config:OAuthConfiguration,status:number,scope:string,code:string): Response {
    const meta=new URL('/.well-known/oauth-protected-resource/mcp',config.resource).href;
    const description=code==='insufficient_scope'?'Additional authorization is required':'Authentication is required';
    const value=`Bearer resource_metadata="${meta}", error="${code}", error_description="${description}"${scope?`, scope="${scope}"`:''}`;
    return new Response(JSON.stringify({error:code}),{status,headers:{...noStore,'Content-Type':'application/json','WWW-Authenticate':value}});
}
/** RFC 6749 2.3.1: form-encode client ID and secret independently before Basic. */
function formEncode(value:string):string {
    return new URLSearchParams({value}).toString().slice('value='.length);
}
function failClosed():Response {
    return new Response(JSON.stringify({error:'authorization_unavailable'}),{status:503,headers:{...noStore,'Content-Type':'application/json'}});
}
/** Only introspect with a fixed, operator-configured authorization server.
 * No bearer token is forwarded to a mailbox API or arbitrary caller URL.
 */
export async function authorizeOAuth(
    config: OAuthConfiguration, request: Request, requiredScope: string,
    accountExists: (accountId: string) => Promise<boolean>,
    fetcher: typeof fetch = fetch, now: number | (() => number) = Date.now
): Promise<OAuthAccess> {
    const raw = request.headers.get('authorization');
    if (!raw || !/^Bearer\s+/i.test(raw)) return challenge(config,401,requiredScope,'invalid_token');
    const match = /^Bearer ([^\s]+)$/i.exec(raw);
    if (!match || match[1].length>4096 || !tokenPattern.test(match[1])) return challenge(config,401,requiredScope,'invalid_token');
    if (new URL(request.url).origin!==new URL(config.resource).origin) return failClosed();
    const form=new URLSearchParams({token:match[1],token_type_hint:'access_token'});
    let data: unknown;
    try {
        const response=await fetcher(config.introspectionUrl,{method:'POST',redirect:'error',cache:'no-store',signal:AbortSignal.timeout(3500),
            headers:{'Authorization':'Basic '+btoa(formEncode(config.clientId)+':'+formEncode(config.clientSecret)),'Content-Type':'application/x-www-form-urlencoded','Accept':'application/json'},body:form});
        if (!response.ok || !/^application\/json(?:;|$)/i.test(response.headers.get('Content-Type')??'')) {await response.body?.cancel();return failClosed();}
        // Bound the remote AS response before JSON parsing; never log secrets or response text.
        const reader=response.body?.getReader(); if(!reader)return failClosed();
        let size=0,text='';const decoder=new TextDecoder('utf-8',{fatal:true});
        try {while(true){const part=await reader.read();if(part.done)break;size+=part.value.byteLength;if(size>8192)return failClosed();text+=decoder.decode(part.value,{stream:true});}text+=decoder.decode();}
        finally {await reader.cancel().catch(()=>{});reader.releaseLock();}
        data=JSON.parse(text);
    } catch {return failClosed();}
    if (!data || typeof data!=='object'||Array.isArray(data)) return challenge(config,401,requiredScope,'invalid_token');
    const info=data as Record<string,unknown>;
    // A token may expire while waiting for remote introspection.
    const clock = typeof now === 'function' ? now : () => now;
    const nowSec=Math.floor(clock()/1000);
    // RFC 7662 does not guarantee issuer/audience in every response; our configured
    // authorization server MUST return both. Ambiguous bindings are rejected.
    if(info.active!==true||info.iss!==config.issuer||info.aud!==config.resource||typeof info.sub!=='string'||!identity.test(info.sub)
        || !Number.isSafeInteger(info.exp)|| (info.exp as number)<=nowSec || (info.exp as number)>nowSec+900
        || !Number.isSafeInteger(info.iat)|| (info.iat as number)>nowSec+60|| (info.iat as number)>=(info.exp as number)
        || (info.exp as number)-(info.iat as number)>900
        || (info.nbf!==undefined&&(!Number.isSafeInteger(info.nbf)||(info.nbf as number)>nowSec))) {
        return challenge(config,401,requiredScope,'invalid_token');
    }
    if (typeof info.scope!=='string'||!info.scope.split(' ').includes(requiredScope)) return challenge(config,403,requiredScope,'insufficient_scope');
    try {if(!await accountExists(info.sub))return challenge(config,401,requiredScope,'invalid_token');}catch{return failClosed();}
    // The database lookup can also outlive the bearer; revalidate before dispatch.
    if((info.exp as number)<=Math.floor(clock()/1000))return challenge(config,401,requiredScope,'invalid_token');
    return {accountId:info.sub};
}
