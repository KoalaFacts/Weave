import { WorkerEntrypoint } from 'cloudflare:workers';
import { createOwnedAgent, humanHasAgent, listHumanAgents, signInHuman, type CreateAgentResult, type SignInResult, type HumanSignIn, type AgentSummary } from './humans.ts';
import { demo } from './demo';
import { agentStart } from './agent-start';
import { registry } from './registry';
import openapi from './openapi.json';
import { publicReadRate } from './public-rate';
import { remoteMcp } from './remote-mcp';
import { oauthConfiguration, authorizeOAuth, protectedResourceMetadata } from './remote-oauth';
import { demoOAuthClientId, demoOAuthClientMetadata } from './oauth-client-metadata';
import { privateMcpTools, privateScope, executePrivateMcp } from './remote-private';
import { directory, mutateAddress, ownedAddresses } from './addresses';
import { sweep } from './cleanup';
import { budgetDatabase } from './budget';
import { stream, type Context } from './stream';
import { registerAgent } from './accounts';
import { authenticateAgent, issueAgentToken } from './agent-tokens.ts';
import { addAgentKey, listAgentKeys, revokeAgentKey } from './agent-keys.ts';
import { cancelBindingRequest, createBindingRequest, getBindingRequest } from './binding-requests.ts';
import { recoverAgentKey } from './recovery-codes.ts';
import { putCard } from './cards';
import { decide, requestContact, setBlocked } from './contacts';
import { deleteContact, ownContacts } from './contact-links';
import { atomic, type Env } from './database';
import { ack, send } from './messages';
import { read } from './reads';
import { body, fail, json, problem } from './wire';
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
            const url = new URL(request.url), method = request.method;
            if (url.protocol !== 'https:' && !['localhost','127.0.0.1','[::1]'].includes(url.hostname)) fail(400,'invalid');
            if (method === 'GET' && url.pathname === '/') return demo();
            if (method === 'GET' && url.pathname === '/agent-start.md') return agentStart();
            if (method === 'GET' && url.pathname === '/registry') return registry();
            if (method === 'GET' && url.pathname === '/openapi.json') return new Response(openapi,{headers:{'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}});
            // Public Client ID Metadata Document for the first-party demo PKCE
            // acceptance client. No agent token, account, cookie or OAuth
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
                        const access=await authorizeOAuth(cfg,request,scope,async accountId=>
                            (await env.db.prepare('SELECT account_id FROM accounts WHERE account_id=?').bind(accountId).first('account_id'))===accountId,
                            undefined,Date.now);
                        return access instanceof Response?access:access.accountId;
                    },
                    invoke:(accountId,name,args)=>executePrivateMcp(env,url.origin,accountId,name,args)
                }:undefined);
            }
            // Key self-registration; registerAgent applies the read-only recovery gate to new keys itself.
            if (method === 'POST' && url.pathname === '/v1/accounts') return json(await registerAgent(env,request,await body(request)));
            if(env.RECOVERY_READ_ONLY==='true'&&(method!=='GET'||url.pathname==='/v1/inbox/events'))fail(503,'maintenance',30);
            if (method === 'POST' && url.pathname === '/v1/token') return await issueAgentToken(env,request);
            // Owner recovery authenticates with the owner-issued code plus the new key's DPoP proof; the agent may have no usable key.
            if (method === 'POST' && url.pathname === '/v1/key-requests') return json(await recoverAgentKey(env,request,await body(request)));
            if(env.RECOVERY_READ_ONLY!=='true'&&method==='GET'&&['/v1/directory','/v1/directory/address','/v1/contacts/cards','/v1/contacts/card'].includes(url.pathname))await publicReadRate(env,request);
            const account = await authenticateAgent(env,request);
            if (method === 'GET' && ['/v1/directory','/v1/directory/address'].includes(url.pathname)) return json(await atomic(env.db,tx => directory(tx,url)));
            if (method === 'GET' && ['/v1/contacts/cards','/v1/contacts/card'].includes(url.pathname)) return json(await atomic(env.db,tx => read(tx,url,account?.accountId ?? null)));
            if (!account) fail(401,'unauthenticated');
            if (url.pathname === '/v1/keys' || url.pathname.startsWith('/v1/keys/')) {
                if (url.pathname === '/v1/keys') {
                    if (method === 'GET') return json(await listAgentKeys(env,account.accountId));
                    if (method === 'POST') { const added = await addAgentKey(env,request,account,await body(request)); return json(added.body,added.status); }
                }
                const kid = /^\/v1\/keys\/([A-Za-z0-9_-]{43})$/.exec(url.pathname)?.[1];
                if (method === 'DELETE' && kid !== undefined) return json(await revokeAgentKey(env,account,kid));
                fail(404,'unavailable');
            }
            if (url.pathname === '/v1/binding-requests' || url.pathname.startsWith('/v1/binding-requests/')) {
                if (method === 'POST' && url.pathname === '/v1/binding-requests') return json(await createBindingRequest(env,request,account,await body(request)));
                const requestId = /^\/v1\/binding-requests\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/.exec(url.pathname)?.[1];
                if (method === 'GET' && requestId !== undefined) return json(await getBindingRequest(env,account,requestId));
                if (method === 'DELETE' && requestId !== undefined) return json(await cancelBindingRequest(env,account,requestId));
                fail(404,'unavailable');
            }
            if (url.pathname === '/v1/addresses' || /^\/v1\/addresses\/[^/]+$/.test(url.pathname)) {
                const id = url.pathname === '/v1/addresses' ? null : url.pathname.split('/')[3];
                if (method === 'GET') return json(await atomic(env.db,tx => ownedAddresses(tx,account.accountId,url)));
                if ((method === 'POST' && id === null) || (method === 'PUT' && id !== null)) return json(await mutateAddress(env,account.accountId,id,await body(request),url.origin));
                if (method === 'DELETE' && id !== null) return json(await mutateAddress(env,account.accountId,id,null,url.origin,true));
                fail(404,'unavailable');
            }
            const match = url.pathname.match(/^\/(inbox|outbox)\/([^/]+)$/);
            if (match) {
                if (decodeURIComponent(match[2]) !== account.accountId) fail(404,'unavailable');
                url.pathname = match[1] === 'outbox' && method === 'POST' ? '/v1/messages' : '/v1/'+match[1];
            }
            const owner = account.accountId;
            if(method==='GET'&&url.pathname==='/v1/contacts')return json(await atomic(env.db,tx=>ownContacts(tx,owner,url)));
            const contact=url.pathname.match(/^\/v1\/contacts\/accounts\/([^/]+)$/);
            if(method==='GET'&&contact)return json(await atomic(env.db,tx=>ownContacts(tx,owner,url,decodeURIComponent(contact[1]))));
            if(method==='DELETE'&&contact)return json(await atomic(env.db,tx=>deleteContact(tx,owner,decodeURIComponent(contact[1]))));
            if (method === 'GET' && url.pathname === '/v1/inbox/events') return await stream(env,owner,request,context);
            if (method === 'GET') return json(await atomic(env.db,tx => read(tx,url,owner)));
            const input = await body(request);
            // Validate bounded payloads before cleanup can produce any write.
            if(input?.payload!==undefined)validatePayload(input.payload);
            if(input?.reply!==undefined&&input.reply!==null)validatePayload(input.reply);
            await sweep(env);
            return json(await atomic(env.db,async tx => {
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
