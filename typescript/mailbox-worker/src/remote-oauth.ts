/** OAuth 2 protected-resource boundary for individually authorized Weave accounts.
 * OAuth 2.1 code/PKCE/consent is performed by the first-party authorization server (the auth Worker),
 * never by a shared mailbox or by MCP tool arguments. Tokens are freshly verified on every protected call
 * through the private Cloudflare Service Binding to that Worker; no positive token cache.
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
    AUTH_SERVER?: OAuthServiceBinding;
}
export interface OAuthOptions { issuer:string; resource:string; authServer:OAuthServiceBinding; }
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
const scopes = ['weave:agents.manage','weave:addresses.read','weave:addresses.write','weave:contacts.read','weave:contacts.write','weave:messages.read','weave:messages.send','weave:messages.ack','weave:actions.read','weave:actions.write'];
const noStore = {'Cache-Control':'no-store','Pragma':'no-cache','X-Content-Type-Options':'nosniff'};
const safeUrl = (raw: string, label: string): URL => {
    const parsed = new URL(raw);
    if (parsed.protocol !== 'https:' || parsed.username || parsed.password || parsed.search || parsed.hash || !parsed.hostname) throw Error('Invalid '+label);
    return parsed;
};
/** A partial/malformed deployment is *not* a fallback to anonymous private tools. */
export function oauthConfiguration(env: OAuthEnvironment, requestUrl: string): OAuthOptions | null {
    if (env.AUTH_SERVER === undefined && env.OAUTH_ISSUER === undefined && env.OAUTH_RESOURCE === undefined) return null;
    if (!env.OAUTH_ISSUER || !env.OAUTH_RESOURCE || !env.AUTH_SERVER || typeof env.AUTH_SERVER.validateToken!=='function')
        throw Error('Invalid OAuth Service Binding configuration');
    const issuer=safeUrl(env.OAUTH_ISSUER,'issuer'), resource=safeUrl(env.OAUTH_RESOURCE,'resource');
    if (resource.pathname!=='/mcp' || resource.href!==env.OAUTH_RESOURCE
        || issuer.href.replace(/\/$/,'')!==env.OAUTH_ISSUER.replace(/\/$/,'')
        || new URL(requestUrl).origin!==resource.origin) throw Error('OAuth Service Binding origin mismatch');
    return {issuer:env.OAUTH_ISSUER,resource:resource.href,authServer:env.AUTH_SERVER};
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
/** A bearer that is valid but names no agent it may act as: not an authentication failure, so no challenge. */
function badAgent(description:string):Response {
    return new Response(JSON.stringify({error:'invalid_agent',error_description:description}),{status:400,headers:{...noStore,'Content-Type':'application/json'}});
}
function failClosed():Response {
    return new Response(JSON.stringify({error:'authorization_unavailable'}),{status:503,headers:{...noStore,'Content-Type':'application/json'}});
}
/** Only the operator-bound private Service Binding validates a token.
 * No bearer token is forwarded to a mailbox API or arbitrary caller URL.
 * Every accepted token must name a live mirrored consent grant that matches its
 * account, human and client; `liveGrant` reads it on every call (no cache).
 */
export async function authorizeOAuth(
    config: OAuthOptions, request: Request, requiredScope: string,
    liveGrant: GrantLookup, now: number | (() => number) = Date.now
): Promise<OAuthAccess> {
    const raw = request.headers.get('authorization');
    if (!raw || !/^Bearer\s+/i.test(raw)) return challenge(config,401,requiredScope,'invalid_token');
    const match = /^Bearer ([^\s]+)$/i.exec(raw);
    if (!match || match[1].length>4096 || !serviceTokenPattern.test(match[1])) return challenge(config,401,requiredScope,'invalid_token');
    if (new URL(request.url).origin!==new URL(config.resource).origin) return failClosed();
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
