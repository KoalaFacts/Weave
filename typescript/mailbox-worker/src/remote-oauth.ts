/** OAuth 2 protected-resource boundary for individually authorized Weave accounts.
 * OAuth 2.1 code/PKCE/consent is performed by the configured authorization server,
 * never by a shared mailbox or by MCP tool arguments. Tokens are freshly verified on every protected call, using HTTPS introspection
 * or an opt-in private Cloudflare Service Binding; no positive token cache. Only Service Binding tokens carry the
 * consent grant that private tools require; HTTPS introspection still validates but never authorizes a private call.
 */
/** Trusted Cloudflare Workers OAuth Provider v1 RPC, not a public introspection URL.
 * Only AS-side grants whose authenticated account UUID is independently verified
 * may place that UUID in both userId and props.accountId. props.grantId names the
 * consent grant the AS mirrored in the mailbox before issuing the authorization code.
 * An owner grant (props.owner true) carries no account: userId and props.humanId are the human, and each call names
 * the agent it acts as in the Weave-Agent header.
 */
export type TokenProps = {accountId:string;humanId:string;grantId:string}|{owner:true;humanId:string;grantId:string};
export interface OAuthServiceBinding {
    validateToken(resource:string,token:string):Promise<{
        audience:string;userId:string;props:TokenProps;
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
/** `accountId` is the agent the call acts as; for an owner grant on the agent-management scope it is the human. */
export type OAuthAccess = {accountId: string; grantId: string} | Response;
/** The mailbox's mirrored consent grant, or null when it is revoked or unknown. An owner grant has no account. */
export type LiveGrant = {accountId: string; humanId: string; clientId: string}
    | {accountId: null; humanId: string; clientId: string; actsAs: (agentId: string) => Promise<boolean>};
export type GrantLookup = (grantId: string) => Promise<LiveGrant | null>;
/** The one scope that manages a human's agents rather than acting as one; only an owner grant may use it. */
const manageScope = 'weave:agents.manage';
const agentHeader = 'Weave-Agent';
const identity = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const serviceTokenPattern = /^[A-Za-z0-9._~+/\-]+:[A-Za-z0-9._~+/\-]+:[A-Za-z0-9._~+/\-]+$/;
const tokenPattern = /^[A-Za-z0-9\-._~+/]+={0,2}$/;
const scopes = ['weave:agents.manage','weave:addresses.read','weave:addresses.write','weave:contacts.read','weave:contacts.write','weave:messages.read','weave:messages.send','weave:messages.ack','weave:actions.read','weave:actions.write'];
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
/** A bearer that is valid but names no agent it may act as: not an authentication failure, so no challenge. */
function badAgent(description:string):Response {
    return new Response(JSON.stringify({error:'invalid_agent',error_description:description}),{status:400,headers:{...noStore,'Content-Type':'application/json'}});
}
function failClosed():Response {
    return new Response(JSON.stringify({error:'authorization_unavailable'}),{status:503,headers:{...noStore,'Content-Type':'application/json'}});
}
/** Only introspect with a fixed, operator-configured authorization server.
 * No bearer token is forwarded to a mailbox API or arbitrary caller URL.
 * Every accepted token must name a live mirrored consent grant that matches its
 * account, human and client; `liveGrant` reads it on every call (no cache).
 */
export async function authorizeOAuth(
    config: OAuthOptions, request: Request, requiredScope: string,
    liveGrant: GrantLookup,
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
        const owner = !!claim && typeof claim.props === 'object' && claim.props !== null && (claim.props as {owner?: unknown}).owner === true;
        if (!claim || claim.audience!==config.resource || typeof claim.userId!=='string'
            || !identity.test(claim.userId) || !claim.props || typeof claim.props!=='object'
            // An agent's grant names its account; an owner grant names only its human, and never also an account.
            || (owner ? 'accountId' in claim.props || claim.props.humanId!==claim.userId : !('accountId' in claim.props) || claim.props.accountId!==claim.userId)
            || typeof claim.props.humanId!=='string' || !identity.test(claim.props.humanId)
            || typeof claim.props.grantId!=='string' || !identity.test(claim.props.grantId)
            || !Number.isSafeInteger(claim.expiresAt)
            || claim.expiresAt<=nowSec || claim.expiresAt>nowSec+900
            || typeof claim.clientId!=='string' || !claim.clientId
            || !Array.isArray(claim.scope) || !claim.scope.every(scope=>typeof scope==='string'))
            return challenge(config,401,requiredScope,'invalid_token');
        // A revoked, unknown or mismatched grant is the same 401 as an invalid token, checked before scope so a
        // revoked token never learns about scopes; storage failure never allows.
        let grant:LiveGrant|null;
        try {grant=await liveGrant(claim.props.grantId);}
        catch {return failClosed();}
        if (!grant || grant.humanId!==claim.props.humanId || grant.clientId!==claim.clientId
            || (owner ? grant.accountId!==null : grant.accountId!==claim.userId))
            return challenge(config,401,requiredScope,'invalid_token');
        if (!claim.scope.includes(requiredScope))return challenge(config,403,requiredScope,'insufficient_scope');
        const named=request.headers.get(agentHeader);
        let accountId=claim.userId;
        if (grant.accountId===null) {
            // Managing agents is the human's own act; acting as an agent names that agent on every call.
            if (requiredScope===manageScope) { if (named!==null) return badAgent(agentHeader+' is not used with agent management.'); }
            else {
                if (named===null || !identity.test(named)) return badAgent('An owner grant names the agent it acts as in '+agentHeader+'.');
                let owned:boolean;
                try {owned=await grant.actsAs(named);}
                catch {return failClosed();}
                // Another human's agent, or none, reads the same as an agent this grant cannot act as.
                if (!owned) return badAgent('The '+agentHeader+' agent is not one this grant can act as.');
                accountId=named;
            }
        } else {
            // An agent's grant acts as its one agent and never manages agents, even if a scope string claims so.
            if (named!==null) return badAgent('This token acts as one agent; '+agentHeader+' is not used.');
            if (requiredScope===manageScope) return challenge(config,403,requiredScope,'insufficient_scope');
        }
        if (claim.expiresAt<=Math.floor(clock()/1000))return challenge(config,401,requiredScope,'invalid_token');
        return {accountId,grantId:claim.props.grantId};
    }
    const form=new URLSearchParams({token:match[1],token_type_hint:'access_token'});
    let data: unknown;
    try {
        // workerd cannot use redirect:'error': manual preserves a non-followed 3xx,
        // which the response.ok gate rejects without forwarding credentials.
        const response=await fetcher(config.introspectionUrl,{method:'POST',redirect:'manual',cache:'no-store',signal:AbortSignal.timeout(3500),
            headers:{'Authorization':'Basic '+btoa(formEncode(config.clientId)+':'+formEncode(config.clientSecret)),'Content-Type':'application/x-www-form-urlencoded','Accept':'application/json'},body:form});
        if (!response.ok || !/^application\/json(?:;|$)/i.test(response.headers.get('Content-Type')??'')) {
            // A failed AS can refuse to finish cancellation; denying access must not wait for it.
            void response.body?.cancel().catch(()=>{});
            return failClosed();
        }
        // Fetch deadlines can finish when headers arrive: bound the *entire* JSON body too.
        const reader=response.body?.getReader(); if(!reader)return failClosed();
        let size=0,text='';const decoder=new TextDecoder('utf-8',{fatal:true});
        let timeout:ReturnType<typeof setTimeout>|undefined;
        const deadline=new Promise<null>(resolve=>{timeout=setTimeout(()=>resolve(null),3500);});
        try {
            while(true){
                const part=await Promise.race([reader.read(),deadline]);
                if(part===null)return failClosed();
                if(part.done)break;
                size+=part.value.byteLength;
                if(size>8192)return failClosed();
                text+=decoder.decode(part.value,{stream:true});
            }
            text+=decoder.decode();
        } finally {
            if(timeout!==undefined)clearTimeout(timeout);
            // Cleanup is best-effort: a stalled body/cancel promise cannot hold the authorization request.
            void reader.cancel().catch(()=>{});
            reader.releaseLock();
        }
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
    // An RFC 7662 response carries no Weave consent grant, so no revocable grant
    // row can stand behind it: introspected tokens never reach private tools.
    return challenge(config,401,requiredScope,'invalid_token');
}
