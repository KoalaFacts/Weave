import { WorkerEntrypoint } from 'cloudflare:workers';
import { createOwnedAgent, humanHasAgent, listHumanAgents, oauthAccountActive, signInHuman, type CreateAgentResult, type SignInResult, type HumanSignIn, type AgentSummary } from './humans.ts';
import { demo } from './demo';
import { agentStart } from './agent-start';
import { registry } from './registry';
import openapi from './openapi.json';
import { publicReadRate } from './public-rate';
import { remoteMcp } from './remote-mcp';
import {managedAccessConfiguration,verifyManagedAccessAssertion,type AssertionRejection,type LifetimeDetail} from './access-managed';
import { oauthConfiguration, authorizeOAuth, protectedResourceMetadata } from './remote-oauth';
import { demoOAuthClientId, demoOAuthClientMetadata } from './oauth-client-metadata';
import type { VerifiedOAuthSubject } from './remote-oauth';
import {resolveLinkedOAuthSubject,linkOAuthSubject,listOAuthLinks,revokeOAuthLink} from './oauth-identity-links';
import { privateMcpTools, privateScope, executePrivateMcp } from './remote-private';
import { directory, mutateAddress, ownedAddresses } from './addresses';
import { sweep } from './cleanup';
import { budgetDatabase } from './budget';
import { stream, type Context } from './stream';
import { authenticate, register } from './accounts';
import { putCard } from './cards';
import { decide, requestContact, setBlocked } from './contacts';
import { deleteContact, ownContacts } from './contact-links';
import { atomic, type Env } from './database';
import { ack, send } from './messages';
import { read } from './reads';
import { body, control, fail, json, legacyResponse, object, problem } from './wire';
import { validatePayload } from './protocol';

// Private Service Binding RPC for the OAuth authorization server; deliberately has no fetch route.
const scopedEnv = (env: Env): Env => ({ ...env, db: budgetDatabase(env.db) });
export class HumanDirectory extends WorkerEntrypoint<Env> {
    signIn(input: HumanSignIn): Promise<SignInResult> { return signInHuman(scopedEnv(this.env), input); }
    listAgents(humanId: string): Promise<AgentSummary[]> { return listHumanAgents(scopedEnv(this.env), humanId); }
    createAgent(input: { humanId: string; displayName: string; sourceIp: string }): Promise<CreateAgentResult> { return createOwnedAgent(scopedEnv(this.env), input); }
    hasAgent(humanId: string, accountId: string): Promise<boolean> { return humanHasAgent(scopedEnv(this.env), humanId, accountId); }
}

export default {
    async scheduled(_event: unknown, env: Env): Promise<void> {
        if(env.MAINTENANCE_MODE==='true'||env.RECOVERY_READ_ONLY==='true')return;
        await sweep({...env,db:budgetDatabase(env.db)},true);
    },
    async fetch(request: Request, env: Env, context: Context): Promise<Response> {
        env = {...env,db:budgetDatabase(env.db)};
        try {
            const url = new URL(request.url), secret = control(request), method = request.method;
            const respond=(value:unknown)=>json(value,200,legacyResponse(request));
            if (url.protocol !== 'https:' && !['localhost','127.0.0.1','[::1]'].includes(url.hostname)) fail(400,'invalid');
            if (method === 'GET' && url.pathname === '/') return demo();
            if (method === 'GET' && url.pathname === '/agent-start.md') return agentStart();
            if (method === 'GET' && url.pathname === '/registry') return registry();
            if (method === 'GET' && url.pathname === '/openapi.json') return new Response(openapi,{headers:{'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}});
            // Public Client ID Metadata Document for the first-party demo PKCE
            // acceptance client. No control secret, account, cookie or OAuth
            // bearer is accepted here. Never reflect an arbitrary Host value.
            if (url.pathname === '/oauth-client-metadata.json') {
                if (method !== 'GET' || url.href !== demoOAuthClientId) fail(404,'unavailable');
                return demoOAuthClientMetadata();
            }
            if(env.MAINTENANCE_MODE==='true')fail(503,'maintenance',30);
            // Protected-resource discovery is an opt-in integration with an
            // external OAuth 2.1 authorization server. Missing/partial config
            // cannot silently enable a shared or anonymous account.
            if(method==='GET'&&(url.pathname==='/.well-known/oauth-protected-resource/mcp'||
                url.pathname==='/.well-known/oauth-protected-resource')){
                let cfg;try{cfg=oauthConfiguration(env,request.url);}catch{fail(503,'storage');}
                if(!cfg)fail(404,'unavailable');
                return protectedResourceMetadata(cfg);
            }

            // Separate hostname-path Access experiment. Do not put the public
            // /mcp or demo root behind Access. Access's opaque OAuth bearer is
            // *not* RFC 7662: origin authorization is the signed Access JWT
            // plus a separately owner-approved persisted Weave subject link.
            if(url.pathname==='/access-trial/mcp'||url.pathname==='/access-trial/link'){
                if(env.RECOVERY_READ_ONLY==='true')fail(503,'maintenance',30);
                let trial;
                try{trial=managedAccessConfiguration(env,request.url);}catch{fail(503,'storage');}
                if(!trial)fail(404,'unavailable');
                if(env.OAUTH_IDENTITY_LINKS_ENABLED!=='true')fail(503,'storage');
                // Only Access-admitted (allowlisted) trial users reach this point.
                // The reason is a fixed category for diagnosing the trial; it
                // never carries claim values, keys, controls or the assertion.
                const denial=(status:number,reason:string,detail?:LifetimeDetail)=>{
                    console.warn(JSON.stringify({event:'access-trial-denied',path:url.pathname,status,reason,detail}));
                    return new Response(JSON.stringify({error:'access_denied',reason,...(detail?{detail}:{})}),{
                        status,headers:{'Content-Type':'application/json','Cache-Control':'no-store',
                            'X-Content-Type-Options':'nosniff'}});
                };
                let rejection:AssertionRejection='malformed',rejectionDetail:LifetimeDetail|undefined;
                const identity=await verifyManagedAccessAssertion(trial,request,fetch,Date.now,(reason,detail)=>{
                    rejection=reason;rejectionDetail=detail;});
                if(!identity)return denial(401,'assertion-'+rejection,rejectionDetail);
                const verifiedSubject:VerifiedOAuthSubject={
                    issuer:identity.issuer,resource:identity.resource,subject:identity.subject
                };
                if(url.pathname==='/access-trial/link'){
                    if(method!=='POST')fail(404,'unavailable');
                    const account=await authenticate(env,secret);
                    if(!account)return denial(401,'account-control');
                    const input=await body(request);
                    object(input,['consent']);
                    if(input.consent!==true)fail(400,'invalid');
                    // Explicit two-credential consent: locally proven control
                    // and signed Access identity. Never forward the control,
                    // bearer or raw Access subject to the OAuth provider or D1.
                    return respond(await linkOAuthSubject(env.db,account.accountId,verifiedSubject));
                }
                return remoteMcp(request,async target=>{
                    try{
                        await publicReadRate(env,request);
                        return json(await atomic(env.db,tx=>directory(tx,target)));
                    }catch(error){return problem(error);}
                },{
                    tools:privateMcpTools,scope:privateScope,
                    // This exception is constructed only after signature/issuer/AUD/expiry
                    // verification above. A client-supplied assertion alone is never a grant.
                    signedAccessAssertionVerified:true,
                    authorize:async _scope=>{
                        if(identity.expiresAt<=Math.floor(Date.now()/1000))return denial(401,'assertion-expired');
                        let accountId:string|null;
                        try{accountId=await resolveLinkedOAuthSubject(env.db,verifiedSubject);}
                        catch{return denial(503,'link-storage');}
                        if(!accountId)return denial(401,'not-linked');
                        try{
                            const found=await env.db.prepare('SELECT account_id FROM accounts WHERE account_id=?')
                                .bind(accountId).first<string>('account_id');
                            if(found!==accountId)return denial(401,'account-missing');
                            if(identity.expiresAt<=Math.floor(Date.now()/1000))return denial(401,'assertion-expired');
                        }catch{return denial(503,'link-storage');}
                        // This isolated test grant is the owner's explicit link
                        // to all ordinary private mailbox tools. The tool's
                        // scope is still resolved and checked before execution.
                        return accountId;
                    },
                    invoke:(accountId,name,args)=>executePrivateMcp(env,url.origin,accountId,name,args)
                });
            }
            if(url.pathname==='/mcp'){
                const cfg=(()=>{try{return oauthConfiguration(env,request.url);}catch{fail(503,'storage');}})();
                return await remoteMcp(request,async target=>{
                    try {
                        if(env.RECOVERY_READ_ONLY!=='true')await publicReadRate(env,request);
                        return json(await atomic(env.db,tx=>directory(tx,target)));
                    } catch(error) { return problem(error); }
                },cfg&&env.RECOVERY_READ_ONLY!=='true'?{
                    tools:privateMcpTools,
                    scope:privateScope,
                    authorize:async scope=>{
                        const resolver=env.OAUTH_IDENTITY_LINKS_ENABLED==='true'?
                            (subject:VerifiedOAuthSubject)=>resolveLinkedOAuthSubject(env.db,subject):undefined;
                        const access=await authorizeOAuth(cfg,request,scope,async (accountId,serviceHumanId)=>
                            oauthAccountActive(env,accountId,serviceHumanId),
                            undefined,Date.now,resolver);
                        return access instanceof Response?access:access.accountId;
                    },
                    invoke:(accountId,name,args)=>executePrivateMcp(env,url.origin,accountId,name,args)
                }:undefined);
            }
            if(env.RECOVERY_READ_ONLY==='true'){
                if(method==='POST'&&url.pathname==='/v1/accounts'){
                    if(!await authenticate(env,secret))fail(401,'unauthenticated');
                }else if(method!=='GET'||url.pathname==='/v1/inbox/events')fail(503,'maintenance',30);
            }
            if (method === 'POST' && url.pathname === '/v1/accounts') return respond(await register(env,request,secret,await body(request)));
            if(env.RECOVERY_READ_ONLY!=='true'&&method==='GET'&&['/v1/directory','/v1/directory/address','/v1/contacts/cards','/v1/contacts/card'].includes(url.pathname))await publicReadRate(env,request);
            const account = await authenticate(env,secret);
            // Owner linking is an explicit two-credential action, never inferred
            // from an OAuth subject, email, token or MCP tool argument. The
            // long-lived control goes only to this Weave resource endpoint.
            if(url.pathname==='/v1/accounts/oauth/links'||url.pathname==='/v1/accounts/oauth/links/revoke'){
                if(env.OAUTH_IDENTITY_LINKS_ENABLED!=='true')fail(404,'unavailable');
                if(!account)fail(401,'unauthenticated');
                if(method==='GET'&&url.pathname==='/v1/accounts/oauth/links'){
                    if([...url.searchParams.keys()].some(key=>key!=='cursor')||
                        url.searchParams.getAll('cursor').length>1)fail();
                    return respond(await listOAuthLinks(env.db,account.accountId,url.searchParams.get('cursor')));
                }
                if(method==='POST'&&url.pathname==='/v1/accounts/oauth/links/revoke'){
                    const input=await body(request);
                    object(input,['issuer','resource','subjectDigest']);
                    return respond(await revokeOAuthLink(env.db,account.accountId,input));
                }
                if(method==='POST'&&url.pathname==='/v1/accounts/oauth/links'){
                    object(await body(request),[]);
                    let cfg;try{cfg=oauthConfiguration(env,request.url);}catch{fail(503,'storage');}
                    // This first path requires standard RFC 7662 introspection.
                    // Native Cloudflare Service Binding accounts already prove UUID ownership.
                    if(!cfg||'mode' in cfg)fail(503,'storage');
                    let subject:VerifiedOAuthSubject|null=null;
                    const access=await authorizeOAuth(cfg,request,'weave:addresses.read',async id=>
                        (await env.db.prepare('SELECT account_id FROM accounts WHERE account_id=?').bind(id).first('account_id'))===id,
                        undefined,Date.now,async verified=>{subject=verified;return account.accountId;});
                    if(access instanceof Response)return access;
                    if(!subject)fail(503,'storage');
                    return respond(await linkOAuthSubject(env.db,account.accountId,subject));
                }
                fail(404,'unavailable');
            }
            if (method === 'GET' && ['/v1/directory','/v1/directory/address'].includes(url.pathname)) return respond(await atomic(env.db,tx => directory(tx,url)));
            if (method === 'GET' && ['/v1/contacts/cards','/v1/contacts/card'].includes(url.pathname)) return respond(await atomic(env.db,tx => read(tx,url,account?.accountId ?? null)));
            if (!account) fail(401,'unauthenticated');
            if (url.pathname === '/v1/addresses' || /^\/v1\/addresses\/[^/]+$/.test(url.pathname)) {
                const id = url.pathname === '/v1/addresses' ? null : url.pathname.split('/')[3];
                if (method === 'GET') return respond(await atomic(env.db,tx => ownedAddresses(tx,account.accountId,url)));
                if ((method === 'POST' && id === null) || (method === 'PUT' && id !== null)) return respond(await mutateAddress(env,account.accountId,id,await body(request),url.origin));
                if (method === 'DELETE' && id !== null) return respond(await mutateAddress(env,account.accountId,id,null,url.origin,true));
                fail(404,'unavailable');
            }
            const match = url.pathname.match(/^\/(inbox|outbox)\/([^/]+)$/);
            if (match) {
                if (decodeURIComponent(match[2]) !== account.accountId) fail(404,'unavailable');
                url.pathname = match[1] === 'outbox' && method === 'POST' ? '/v1/messages' : '/v1/'+match[1];
            }
            const owner = account.accountId;
            if(method==='GET'&&url.pathname==='/v1/contacts')return respond(await atomic(env.db,tx=>ownContacts(tx,owner,url)));
            const contact=url.pathname.match(/^\/v1\/contacts\/accounts\/([^/]+)$/);
            if(method==='GET'&&contact)return respond(await atomic(env.db,tx=>ownContacts(tx,owner,url,decodeURIComponent(contact[1]))));
            if(method==='DELETE'&&contact)return respond(await atomic(env.db,tx=>deleteContact(tx,owner,decodeURIComponent(contact[1]))));
            if (method === 'GET' && url.pathname === '/v1/inbox/events') return await stream(env,owner,request,context);
            if (method === 'GET') return respond(await atomic(env.db,tx => read(tx,url,owner)));
            const input = await body(request);
            // Validate bounded payloads before cleanup can produce any write.
            if(input?.payload!==undefined)validatePayload(input.payload);
            if(input?.reply!==undefined&&input.reply!==null)validatePayload(input.reply);
            await sweep(env);
            return respond(await atomic(env.db,async tx => {
                if (method === 'PUT' && url.pathname === '/v1/contacts/cards') return putCard(tx,owner,input);
                if (method === 'POST' && url.pathname === '/v1/contacts/requests') return requestContact(tx,owner,input);
                const decision = url.pathname.match(/^\/v1\/contacts\/requests\/([^/]+)\/decision$/);
                if (method === 'POST' && decision) return decide(tx,owner,decision[1],input);
                if (['PUT','DELETE'].includes(method) && url.pathname === '/v1/contacts/blocks') return setBlocked(tx,owner,input,method === 'PUT');
                if (method === 'POST' && url.pathname === '/v1/messages') return send(tx,owner,input);
                const acknowledge = url.pathname.match(/^\/v1\/inbox\/([^/]+)\/ack$/);
                if (method === 'POST' && acknowledge) return ack(tx,owner,acknowledge[1],input);
                fail(404,'unavailable');
            }));
        } catch (error) { return problem(error); }
    }
};
