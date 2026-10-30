/** OAuth 2 protected-resource boundary for individually authorized Weave accounts.
 * OAuth 2.1 code/PKCE/consent is performed by the configured authorization server,
 * never by a shared mailbox or by MCP tool arguments. Tokens are freshly verified on every protected call, using HTTPS introspection
 * or an opt-in private Cloudflare Service Binding; no positive token cache.
 */
/** Trusted Cloudflare Workers OAuth Provider v1 RPC, not a public introspection URL.
 * Only AS-side grants whose authenticated account UUID is independently verified
 * may place that UUID in both userId and props.accountId.
 */
export interface OAuthServiceBinding {
    validateToken(resource:string,token:string):Promise<{
        audience:string;userId:string;props:{accountId:string};
        expiresAt:number;scope:string[];clientId:string;
    }|null>;
}
export interface OAuthEnvironment {
    OAUTH_ISSUER?: string;
    OAUTH_RESOURCE?: string;
    OAUTH_INTROSPECTION_URL?: string;
    /** Operator-approved exact HTTPS origin if RFC 7662 lives outside the issuer origin. */
    OAUTH_INTROSPECTION_TRUSTED_ORIGIN?: string;
    OAUTH_CLIENT_ID?: string;
    OAUTH_CLIENT_SECRET?: string;
    AUTH_SERVER?: OAuthServiceBinding;
}
export interface OAuthConfiguration {
    issuer: string;
    resource: string;
    introspectionUrl: string;
    clientId: string;
    clientSecret: string;
}
export interface OAuthServiceConfiguration { mode:'service'; issuer:string; resource:string; authServer:OAuthServiceBinding; }
export type OAuthOptions = OAuthConfiguration | OAuthServiceConfiguration;
export type OAuthAccess = {accountId: string} | Response;
const identity = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const serviceTokenPattern = /^[A-Za-z0-9._~+/\-]+:[A-Za-z0-9._~+/\-]+:[A-Za-z0-9._~+/\-]+$/;
const tokenPattern = /^[A-Za-z0-9\-._~+/]+={0,2}$/;
const scopes = ['weave:addresses.read','weave:addresses.write','weave:contacts.read','weave:contacts.write','weave:messages.read','weave:messages.send','weave:messages.ack'];
const noStore = {'Cache-Control':'no-store','Pragma':'no-cache','X-Content-Type-Options':'nosniff'};
const safeUrl = (raw: string, label: string): URL => {
    const parsed = new URL(raw);
    if (parsed.protocol !== 'https:' || parsed.username || parsed.password || parsed.search || parsed.hash || !parsed.hostname) throw Error('Invalid '+label);
    return parsed;
};
/** A partial/malformed deployment is *not* a fallback to anonymous private tools. */
export function oauthConfiguration(env: OAuthEnvironment, requestUrl: string): OAuthOptions | null {
    const values = [env.OAUTH_ISSUER,env.OAUTH_RESOURCE,env.OAUTH_INTROSPECTION_URL,env.OAUTH_CLIENT_ID,env.OAUTH_CLIENT_SECRET];
    const trustedOrigin=env.OAUTH_INTROSPECTION_TRUSTED_ORIGIN;
    if (env.AUTH_SERVER !== undefined) {
        if (!env.OAUTH_ISSUER || !env.OAUTH_RESOURCE || trustedOrigin!==undefined || values.slice(2).some(value=>value!==undefined)
            || !env.AUTH_SERVER || typeof env.AUTH_SERVER.validateToken!=='function') throw Error('Invalid OAuth Service Binding configuration');
        const issuer=safeUrl(env.OAUTH_ISSUER,'issuer'), resource=safeUrl(env.OAUTH_RESOURCE,'resource');
        if (resource.pathname!=='/mcp' || resource.href!==env.OAUTH_RESOURCE
            || issuer.href.replace(/\/$/,'')!==env.OAUTH_ISSUER.replace(/\/$/,'')
            || new URL(requestUrl).origin!==resource.origin) throw Error('OAuth Service Binding origin mismatch');
        return {mode:'service',issuer:env.OAUTH_ISSUER,resource:resource.href,authServer:env.AUTH_SERVER};
    }
    if (values.every(v=>v===undefined)) {
        if (trustedOrigin!==undefined)throw Error('Incomplete OAuth configuration');
        return null;
    }
    if (values.some(v=>!v)) throw Error('Incomplete OAuth configuration');
    const issuer=safeUrl(values[0]!, 'issuer'),resource=safeUrl(values[1]!, 'resource'),introspection=safeUrl(values[2]!, 'introspection');
    // Cross-origin RFC 7662 is operator opt-in: introspection credentials
    // are sent only to this exact preconfigured HTTPS host, never a redirect.
    let allowedIntrospectionOrigin=issuer.origin;
    if(trustedOrigin!==undefined){
        const approved=safeUrl(trustedOrigin,'trusted introspection origin');
        if(approved.origin!==trustedOrigin)throw Error('Invalid trusted introspection origin');
        allowedIntrospectionOrigin=approved.origin;
    }
    if (resource.pathname!=='/mcp'||resource.href!==values[1]||issuer.href.replace(/\/$/,'')!==values[0]!.replace(/\/$/,'')||introspection.origin!==allowedIntrospectionOrigin) throw Error('OAuth binding mismatch');
    if (new URL(requestUrl).origin!==resource.origin) throw Error('Unexpected OAuth resource origin');
    if (!/^[\x21-\x7e]{1,128}$/.test(values[3]!)||values[3]!.includes(':')|| !/^[\x21-\x7e]{16,2048}$/.test(values[4]!)) throw Error('Invalid OAuth client credentials');
    return {issuer:values[0]!,resource:resource.href,introspectionUrl:introspection.href,clientId:values[3]!,clientSecret:values[4]!};
}
export function protectedResourceMetadata(config: OAuthOptions): Response {
    return new Response(JSON.stringify({resource:config.resource,authorization_servers:[config.issuer],bearer_methods_supported:['header'],scopes_supported:scopes}),
        {status:200,headers:{...noStore,'Content-Type':'application/json'}});
}
function challenge(config:OAuthOptions,status:number,scope:string,code:string): Response {
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
    config: OAuthOptions, request: Request, requiredScope: string,
    accountExists: (accountId: string) => Promise<boolean>,
    fetcher: typeof fetch = fetch, now: number | (() => number) = Date.now
): Promise<OAuthAccess> {
    const raw = request.headers.get('authorization');
    if (!raw || !/^Bearer\s+/i.test(raw)) return challenge(config,401,requiredScope,'invalid_token');
    const match = /^Bearer ([^\s]+)$/i.exec(raw);
    if (!match || match[1].length>4096 || !(('mode' in config ? serviceTokenPattern : tokenPattern).test(match[1]))) return challenge(config,401,requiredScope,'invalid_token');
    if (new URL(request.url).origin!==new URL(config.resource).origin) return failClosed();
    if ('mode' in config) {
        // Cloudflare's validated claims are trusted only from this operator-bound
        // private RPC. Never treat model arguments or user-submitted account IDs
        // as authorization. Recheck expiry after every asynchronous boundary.
        const clock=typeof now==='function'?now:()=>now;
        let claim:Awaited<ReturnType<OAuthServiceBinding['validateToken']>>;
        try { claim=await config.authServer.validateToken(config.resource,match[1]); }
        catch { return failClosed(); }
        const nowSec=Math.floor(clock()/1000);
        if (!claim || claim.audience!==config.resource || typeof claim.userId!=='string'
            || !identity.test(claim.userId) || !claim.props || typeof claim.props!=='object'
            || claim.props.accountId!==claim.userId || !Number.isSafeInteger(claim.expiresAt)
            || claim.expiresAt<=nowSec || claim.expiresAt>nowSec+900
            || typeof claim.clientId!=='string' || !claim.clientId
            || !Array.isArray(claim.scope) || !claim.scope.every(scope=>typeof scope==='string'))
            return challenge(config,401,requiredScope,'invalid_token');
        if (!claim.scope.includes(requiredScope))return challenge(config,403,requiredScope,'insufficient_scope');
        try {if(!await accountExists(claim.userId))return challenge(config,401,requiredScope,'invalid_token');}
        catch {return failClosed();}
        if (claim.expiresAt<=Math.floor(clock()/1000))return challenge(config,401,requiredScope,'invalid_token');
        return {accountId:claim.userId};
    }
    const form=new URLSearchParams({token:match[1],token_type_hint:'access_token'});
    let data: unknown;
    try {
        // workerd cannot use redirect:'error': manual preserves a non-followed 3xx,
        // which the response.ok gate rejects without forwarding credentials.
        const response=await fetcher(config.introspectionUrl,{method:'POST',redirect:'manual',cache:'no-store',signal:AbortSignal.timeout(3500),
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
    if(info.active!==true||info.iss!==config.issuer||info.aud!==config.resource||typeof info.sub!=='string'||info.sub.length<1||info.sub.length>255||/[\x00-\x1f\x7f]/.test(info.sub)
        || !Number.isSafeInteger(info.exp)|| (info.exp as number)<=nowSec || (info.exp as number)>nowSec+900
        || !Number.isSafeInteger(info.iat)|| (info.iat as number)>nowSec+60|| (info.iat as number)>=(info.exp as number)
        || (info.exp as number)-(info.iat as number)>900
        || (info.nbf!==undefined&&(!Number.isSafeInteger(info.nbf)||(info.nbf as number)>nowSec))) {
        return challenge(config,401,requiredScope,'invalid_token');
    }
    if (typeof info.scope!=='string'||!info.scope.split(' ').includes(requiredScope)) return challenge(config,403,requiredScope,'insufficient_scope');
    // The configured authorization server is explicitly trusted to issue the
    // Weave account UUID as `sub`; anything else is rejected.
    if(!identity.test(info.sub))return challenge(config,401,requiredScope,'invalid_token');
    const accountId=info.sub;
    if((info.exp as number)<=Math.floor(clock()/1000))return challenge(config,401,requiredScope,'invalid_token');
    try {if(!await accountExists(accountId))return challenge(config,401,requiredScope,'invalid_token');}catch{return failClosed();}
    // The account lookup can also outlive the bearer; revalidate before dispatch.
    if((info.exp as number)<=Math.floor(clock()/1000))return challenge(config,401,requiredScope,'invalid_token');
    return {accountId};
}
