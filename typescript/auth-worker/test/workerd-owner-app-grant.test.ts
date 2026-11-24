import assert from "node:assert/strict";
import {createHash,createHmac,randomBytes,randomUUID} from "node:crypto";
import {readFileSync} from "node:fs";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {test} from "node:test";
import {Miniflare} from "miniflare";
import {initializeDatabase} from "../../mailbox-worker/scripts/latest-schema.ts";
import {demoOAuthClientId,demoOAuthClientMetadata} from "../../mailbox-worker/src/oauth-client-metadata.ts";

const issuer="https://auth.weave.example";
const resource="https://demo.weave-389.workers.dev/mcp";
const redirectUri="http://127.0.0.1:53621/callback";
const agentScope="weave:addresses.read weave:addresses.write weave:contacts.read weave:contacts.write weave:messages.read weave:messages.send weave:messages.ack";
// The owner app also proposes external actions as its agents, so the push webhook path can be driven end to end.
const ownerScope="weave:agents.manage weave:actions.read weave:actions.write "+agentScope;
const githubClientSecret=randomBytes(20).toString("hex");
const sessionKey=randomBytes(32).toString("base64url");
const bundled=readFileSync(resolve("dist/index.js"),"utf8");
const mailboxBundle=readFileSync(resolve("../mailbox-worker/dist/index.js"),"utf8");
const authName="weave-auth-owner",mailboxName="weave-mailbox-owner",rpcName="weave-rpc-owner";
// A test-only caller that reaches the mailbox HumanDirectory entrypoint exactly as the AS does.
const rpcWorker=`export default {async fetch(request,env){return Response.json(await env.MAILBOX.revokeGrant(await request.json()));}};`;

// Fake GitHub: the code "gh-<id>" signs in GitHub user <id> as login "human<id>".
function outbound(request:Request):Response|Promise<Response>{
  if(request.method==="GET"&&request.url===demoOAuthClientId)return demoOAuthClientMetadata();
  return fakeGithub(request);
}
async function fakeGithub(request:Request):Promise<Response>{
  if(request.method==="POST"&&request.url==="https://github.com/login/oauth/access_token"){
    const form=new URLSearchParams(await request.text());
    const id=/^gh-([1-9][0-9]{0,8})$/.exec(form.get("code")??"")?.[1];
    if(form.get("client_secret")!==githubClientSecret||!id)return Response.json({error:"bad_verification_code"});
    return Response.json({access_token:"gho_test_"+id,token_type:"bearer",scope:"read:user"});
  }
  if(request.method==="GET"&&request.url==="https://api.github.com/user"){
    const id=/^Bearer gho_test_([0-9]+)$/.exec(request.headers.get("authorization")??"")?.[1];
    if(!id)return new Response("Bad credentials",{status:401});
    return Response.json({id:Number(id),login:"human"+id,created_at:"2011-01-25T18:44:36Z"});
  }
  return new Response("Outbound denied",{status:403});
}

// The owner app's fake webhook receiver (push webhook W3); the mailbox reaches nothing else.
const webhookPosts:{url:string;headers:Headers;body:string}[]=[];
const providerPosts:string[]=[];
async function mailboxOutbound(request:Request):Promise<Response>{
  if(request.method==="POST"&&request.url.startsWith("https://office.example/")){
    webhookPosts.push({url:request.url,headers:request.headers,body:await request.text()});
    return new Response(null,{status:204});
  }
  // The test-only provider: it completes every send, so an approved plan can be executed.
  if(request.method==="POST"&&request.url==="https://provider.test/post"){
    providerPosts.push(await request.text());
    return Response.json({externalId:"post-"+providerPosts.length});
  }
  return new Response("Outbound denied",{status:403});
}

function options(storage:string){
  const common={compatibilityDate:"2026-10-13",compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"]};
  return {
    resourcePersistencePath:storage,cf:false,
    workers:[{
      dev:{outboundService:{type:"fetcher",handler:outbound}},
      config:{
        name:authName,...common,
        manifest:{mainModule:"index.js",modules:{"index.js":{type:"esm",contents:bundled}}},
        env:{
          OAUTH_KV:{type:"kv"},
          CONSENT_NONCES:{type:"durable-object",worker:authName,exportName:"ConsentNonce"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          GITHUB_CLIENT_ID:{type:"text",value:"Iv1.test-client-id"},
          GITHUB_CLIENT_SECRET:{type:"text",value:githubClientSecret},
          HUMAN_SESSION_KEY:{type:"text",value:sessionKey},
          MAILBOX:{type:"worker",worker:mailboxName,exportName:"HumanDirectory"}
        },
        exports:{ConsentNonce:{type:"durable-object",storage:"sqlite"}}
      }
    },{
      dev:{outboundService:{type:"fetcher",handler:mailboxOutbound}},
      config:{
        name:mailboxName,...common,
        manifest:{mainModule:"mailbox.js",modules:{"mailbox.js":{type:"esm",contents:mailboxBundle}}},
        env:{
          AUTH_SERVER:{type:"worker",worker:authName},
          db:{type:"d1"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          REGISTRATION_ENABLED:{type:"text",value:"true"},
          REGISTRATION_MAX_ACCOUNTS:{type:"text",value:"1000"},
          REGISTRATION_PER_IP_PER_HOUR:{type:"text",value:"1000"},
          REGISTRATION_GLOBAL_PER_HOUR:{type:"text",value:"1000"},
          CLEANUP_MAX_MESSAGES:{type:"text",value:"0"},
          CLEANUP_TIME_BUDGET_MS:{type:"text",value:"0"},
          EXTERNAL_CREDENTIAL_KEY:{type:"text",value:randomBytes(32).toString("base64url")},
          // Registers the test-only provider so a plan can be proposed; nothing is ever executed here.
          EXTERNAL_ACTION_TEST_ENDPOINT:{type:"text",value:"https://provider.test/post"}
        }
      }
    },{
      config:{
        name:rpcName,...common,
        manifest:{mainModule:"rpc.js",modules:{"rpc.js":{type:"esm",contents:rpcWorker}}},
        env:{MAILBOX:{type:"worker",worker:mailboxName,exportName:"HumanDirectory"}}
      }
    }]
  };
}

const cookiePairs=(headers:{getSetCookie():string[]},prefix:string)=>
  headers.getSetCookie().filter(x=>x.startsWith(prefix)).map(x=>x.split(";")[0]);
type Consent={verifier:string;handle:string;cookie:string;page:string;session:string};
/** Sign in through fake GitHub and open the consent page for a request with the given scope. */
async function consent(mf:Miniflare,githubId:number,scope:string):Promise<Consent>{
  const verifier=randomBytes(32).toString("base64url");
  const url=new URL(issuer+"/authorize");
  for(const [k,v] of Object.entries({response_type:"code",client_id:demoOAuthClientId,redirect_uri:redirectUri,scope,state:"state-"+randomUUID(),resource,
    code_challenge:createHash("sha256").update(verifier).digest("base64url"),code_challenge_method:"S256"}))url.searchParams.set(k,v);
  const begin=await mf.dispatchFetch(url.href,{redirect:"manual"});
  assert.equal(begin.status,302);
  const github=new URL(begin.headers.get("location")!);
  const [login]=cookiePairs(begin.headers,"__Host-weave_login=");
  const back=await mf.dispatchFetch(issuer+"/login/github/callback?"+new URLSearchParams({code:"gh-"+githubId,state:github.searchParams.get("state")!}),
    {redirect:"manual",headers:{Cookie:login}});
  assert.equal(back.status,302);
  const [session]=cookiePairs(back.headers,"__Host-weave_human=");
  const page=await mf.dispatchFetch(url.href,{redirect:"manual",headers:{Cookie:session}});
  assert.equal(page.status,200);
  const html=await page.text();
  const handle=html.match(/name="handle" value="([A-Za-z0-9_-]{43})"/)![1];
  return {verifier,handle,page:html,session,cookie:[session,...page.headers.getSetCookie().map(x=>x.split(";")[0])].join("; ")};
}
const approve=(mf:Miniflare,c:Consent,fields:Record<string,string>)=>mf.dispatchFetch(issuer+"/authorize",{method:"POST",redirect:"manual",
  headers:{Origin:issuer,Cookie:c.cookie,"Content-Type":"application/x-www-form-urlencoded"},
  body:new URLSearchParams({handle:c.handle,decision:"approve",...fields}).toString()});
async function exchange(mf:Miniflare,c:Consent,approved:Awaited<ReturnType<typeof approve>>):Promise<string>{
  assert.equal(approved.status,302,await approved.clone().text());
  const code=new URL(approved.headers.get("location")!).searchParams.get("code");
  assert.ok(code);
  const {token_endpoint}=await (await mf.dispatchFetch(issuer+"/.well-known/oauth-authorization-server")).json() as {token_endpoint:string};
  const token=await mf.dispatchFetch(token_endpoint,{method:"POST",redirect:"manual",headers:{"Content-Type":"application/x-www-form-urlencoded"},
    body:new URLSearchParams({grant_type:"authorization_code",code,redirect_uri:redirectUri,code_verifier:c.verifier,client_id:demoOAuthClientId,resource}).toString()});
  assert.equal(token.status,200,await token.clone().text());
  return (await token.json() as {access_token:string}).access_token;
}

let rpcId=100;
async function mcp(mf:Miniflare,token:string,name:string,args:Record<string,unknown>={},agent?:string):Promise<Response>{
  const mailbox=await mf.getWorker(mailboxName);
  const headers:Record<string,string>={"Content-Type":"application/json",Accept:"application/json, text/event-stream",
    "MCP-Protocol-Version":"2025-11-25",Authorization:"Bearer "+token};
  if(agent!==undefined)headers["Weave-Agent"]=agent;
  return mailbox.fetch(resource,{method:"POST",headers,
    body:JSON.stringify({jsonrpc:"2.0",id:rpcId++,method:"tools/call",params:{name,arguments:args}})}) as unknown as Promise<Response>;
}
type Frame={result?:{isError?:boolean;structuredContent?:unknown;content?:{text:string}[]}};
async function call(mf:Miniflare,token:string,name:string,args:Record<string,unknown>={},agent?:string):Promise<any>{
  const response=await mcp(mf,token,name,args,agent);
  assert.equal(response.status,200,name+" must be accepted");
  const frame=await response.json() as Frame;
  assert.ok(frame.result&&!frame.result.isError,name+" failed: "+JSON.stringify(frame));
  return frame.result.structuredContent??JSON.parse(frame.result.content![0].text);
}
/** A refused call: the tool error text, which carries the stable code. */
async function refused(mf:Miniflare,token:string,name:string,args:Record<string,unknown>={},agent?:string):Promise<string>{
  const response=await mcp(mf,token,name,args,agent);
  assert.equal(response.status,200,name+" answers as a tool error");
  const frame=await response.json() as Frame;
  assert.ok(frame.result?.isError,name+" must be refused: "+JSON.stringify(frame));
  return JSON.stringify(frame.result);
}

test("ownerGrant_ConsentCreateActAsRevoke_EndToEnd",{timeout:240_000},async t=>{
  const storage=await mkdtemp(join(tmpdir(),"weave-auth-owner-"));
  const mf=new Miniflare(options(storage));
  t.after(async()=>{
    try{await mf.dispose();}
    finally{await rm(storage,{recursive:true,force:true,maxRetries:10,retryDelay:200});}
  });
  await initializeDatabase(await mf.getD1Database("db",mailboxName),BigInt(Date.now())*10000n);

  // Another human's agent, connected the usual way, which the owner app must never reach.
  const other=await consent(mf,8202,agentScope);
  assert.doesNotMatch(other.page,/manage all of your Weave agents/);
  const otherToken=await exchange(mf,other,await approve(mf,other,{agent:"new",agentName:"Other agent"}));
  const otherId=(await call(mf,otherToken,"get_my_profile")).id as string;

  // The owner request shows the owner page; it can be approved only for all agents, and "all" only for it.
  const wrong=await consent(mf,8201,ownerScope);
  assert.match(wrong.page,/manage all of your Weave agents/);
  assert.match(wrong.page,/name="agent" value="all"/);
  assert.equal((await approve(mf,wrong,{agent:"new",agentName:"One agent"})).status,400);
  const plain=await consent(mf,8201,agentScope);
  assert.equal((await approve(mf,plain,{agent:"all"})).status,400);
  const db=await mf.getD1Database("db",mailboxName);
  assert.equal((await db.prepare("SELECT count(*) AS n FROM owner_grants").first<{n:number}>())!.n,0,"no refused approval recorded a grant");

  const owner=await consent(mf,8201,ownerScope);
  const token=await exchange(mf,owner,await approve(mf,owner,{agent:"all"}));

  // Manage: create, list, rename, without naming an agent.
  const created=await call(mf,token,"create_agent",{displayName:"Front desk"});
  assert.equal(created.displayName,"Front desk");
  const listed=await call(mf,token,"list_agents");
  assert.deepEqual(listed.items.map((x:{accountId:string})=>x.accountId),[created.accountId]);
  assert.equal((await call(mf,token,"rename_agent",{accountId:created.accountId,displayName:"Reception"})).displayName,"Reception");
  assert.match(await refused(mf,token,"rename_agent",{accountId:otherId,displayName:"Taken"}),/agent-not-found/);

  // Act as the new agent by naming it; naming none, or another human's agent, is refused.
  assert.equal((await call(mf,token,"get_my_profile",{},created.accountId)).id,created.accountId);
  const address=randomUUID();
  await call(mf,token,"ensure_my_address",{addressId:address,label:"owner-app-reception"},created.accountId);
  assert.match(await refused(mf,token,"get_my_profile"),/invalid_agent/);
  assert.match(await refused(mf,token,"get_my_profile",{},otherId),/invalid_agent/);
  // A token for one agent can neither name an agent nor manage agents.
  assert.match(await refused(mf,otherToken,"get_my_profile",{},otherId),/invalid_agent/);
  const manage=await mcp(mf,otherToken,"list_agents");
  assert.equal(manage.status,403,"an agent token never manages agents");

  // Push webhook registrations belong to the owner grant: the secret is returned once, never listed, and an agent token
  // cannot register one.
  const hook=await call(mf,token,"register_webhook",{url:"https://office.example/weave-hook"});
  assert.match(hook.secret,/^[A-Za-z0-9_-]{43}$/);
  const hooks=await call(mf,token,"list_webhooks");
  assert.deepEqual(hooks.items,[{webhookId:hook.webhookId,url:"https://office.example/weave-hook",createdAt:hook.createdAt}]);
  assert.equal(JSON.stringify(hooks).includes(hook.secret),false);
  assert.match(await refused(mf,token,"register_webhook",{url:"http://office.example/plain"}),/url/);
  assert.equal((await mcp(mf,otherToken,"register_webhook",{url:"https://other.example/hook"})).status,403,"an agent token never registers webhooks");
  assert.deepEqual(await call(mf,token,"revoke_webhook",{webhookId:hook.webhookId}),{webhookId:hook.webhookId,revoked:true});
  assert.deepEqual((await call(mf,token,"list_webhooks")).items,[]);
  const second=await call(mf,token,"register_webhook",{url:"https://office.example/second"});

  // A status change of an agent's action reaches the live webhook, signed with the secret register_webhook returned,
  // and only the live one: the scheduled sender runs on the mailbox worker.
  const at=(BigInt(Date.now())*10000n).toString();
  await db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
    VALUES(?,'desk','test-http','rev-1','urn:test:desk','Desk',CAST(? AS INTEGER),CAST(? AS INTEGER))`).bind(created.accountId,at,at).run();
  const proposed=await call(mf,token,"propose_external_action",{credentialRef:"desk",operation:"publish_text",content:"Doors open at nine."},created.accountId);
  const sweep=await (await mf.getWorker(mailboxName)).scheduled({cron:"*/5 * * * *",scheduledTime:new Date()});
  assert.equal(sweep.outcome,"ok");
  assert.equal(webhookPosts.length,1,"one delivery, to the live webhook only");
  const [post]=webhookPosts;
  assert.equal(post.url,"https://office.example/second");
  const eventId=`${second.webhookId}:${proposed.actionId}:pending`;
  assert.equal(post.headers.get("weave-event-id"),eventId);
  const body=JSON.parse(post.body);
  assert.deepEqual([body.eventId,body.type,body.webhookId,body.actionId,body.status],[eventId,"external_action.status",second.webhookId,proposed.actionId,"pending"]);
  assert.equal(post.body.includes("Doors open"),false,"never the content");
  const signed=/^t=(\d+),v1=([0-9a-f]{64})$/.exec(post.headers.get("weave-signature")??"");
  assert.ok(signed);
  assert.equal(signed[2],createHmac("sha256",second.secret).update(`${signed[1]}.${post.body}`).digest("hex"));

  // An agent the owner gets later is covered without a new consent.
  const later=await call(mf,token,"create_agent",{displayName:"Back office"});
  assert.equal((await call(mf,token,"get_my_profile",{},later.accountId)).id,later.accountId);

  // The account page lists the app; revoking it ends the very next call, for every agent, and leaves the other human alone.
  const accountPage=await (await mf.dispatchFetch(issuer+"/account",{headers:{Cookie:owner.session}})).text();
  assert.match(accountPage,/Apps that manage all your agents/);
  // Push webhook W4: the page names the app's live callback (across the two Workers' RPC), never the revoked one or a secret.
  assert.ok(accountPage.includes("Weave sends it the status of your agents' actions (never their content) at:</p><ul><li><code>https://office.example/second</code>"),accountPage);
  assert.ok(!accountPage.includes("weave-hook"),"a revoked callback is not listed");
  assert.ok(!accountPage.includes(second.secret)&&!accountPage.includes(hook.secret),"never a signing secret");
  const grant=await db.prepare("SELECT grant_id,human_id FROM owner_grants WHERE revoked_at IS NULL").first<{grant_id:string;human_id:string}>();
  assert.ok(grant);
  assert.ok(accountPage.includes("revoke-grant:"+grant.grant_id)||accountPage.includes("Revoke access"));
  const rpc=await mf.getWorker(rpcName);
  const revoked=await rpc.fetch("https://rpc.internal/",{method:"POST",body:JSON.stringify({humanId:grant.human_id,grantId:grant.grant_id})});
  assert.deepEqual(await revoked.json(),{ok:true});
  for(const agent of [created.accountId,later.accountId]){
    const denied=await mcp(mf,token,"get_my_profile",{},agent);
    assert.equal(denied.status,401);
    assert.match(denied.headers.get("www-authenticate")??"",/invalid_token/);
  }
  assert.equal((await mcp(mf,token,"list_agents")).status,401);
  assert.equal((await mcp(mf,token,"revoke_webhook",{webhookId:second.webhookId})).status,401,"a revoked grant cannot touch its webhooks");
  assert.equal((await db.prepare("SELECT count(*) AS n FROM app_webhooks WHERE grant_id=?").bind(grant.grant_id).first<{n:number}>())!.n,2,"every registration stays recorded");
  assert.equal((await call(mf,otherToken,"get_my_profile")).id,otherId,"the other human's client still works");

  // A1-1: letting the app approve or reject is a box on the owner consent only, recorded with the grant, shown on /account
  // and stopped there without ending the app's other access.
  const counts=async()=>Promise.all(["accounts","client_grants","owner_grants","owner_grant_approvals"].map(async table=>
    (await db.prepare("SELECT count(*) AS n FROM "+table).first<{n:number}>())!.n));
  const before=await counts();
  const agentWithBox=await consent(mf,8202,agentScope);
  assert.equal((await approve(mf,agentWithBox,{agent:"new",agentName:"Boxed",appDecides:"yes"})).status,400,"an agent's own grant can never decide");
  const agentAllWithBox=await consent(mf,8202,agentScope);
  assert.equal((await approve(mf,agentAllWithBox,{agent:"all",appDecides:"yes"})).status,400,"nor can an agent request approved for all agents");
  assert.deepEqual(await counts(),before,"a refused box creates no agent, grant or permission");
  const deciding=await consent(mf,8201,ownerScope);
  assert.match(deciding.page,/<input type="checkbox" name="appDecides" value="yes">/);
  const decidingToken=await exchange(mf,deciding,await approve(mf,deciding,{agent:"all",appDecides:"yes"}));
  const live=await db.prepare("SELECT g.grant_id,a.human_id,a.revoked_at FROM owner_grants g JOIN owner_grant_approvals a ON a.grant_id=g.grant_id WHERE g.revoked_at IS NULL")
    .all<{grant_id:string;human_id:string;revoked_at:number|null}>();
  assert.equal(live.results.length,1);
  assert.equal(live.results[0]!.human_id,grant.human_id);
  assert.equal(live.results[0]!.revoked_at,null);
  assert.equal((await db.prepare("SELECT count(*) AS n FROM owner_grant_approvals").first<{n:number}>())!.n,1,"neither the first owner grant nor the agent's grant was given it");
  // A1-2: that app reads a pending plan of the human's agent as the owner page shows it and approves that exact plan; the
  // agent then executes it once. The decision is the human's, and the grant that made it is recorded with it.
  const queued=await call(mf,decidingToken,"propose_external_action",{credentialRef:"desk",operation:"publish_text",content:"Lunch is served."},created.accountId);
  const plan=await call(mf,decidingToken,"get_pending_action",{actionId:queued.actionId});
  assert.equal(plan.content,"Lunch is served.");
  const waiting=await call(mf,decidingToken,"list_pending_actions",{});
  assert.deepEqual(waiting.items.find((item:{actionId:string})=>item.actionId===queued.actionId),plan,"the app lists what waits across the human's agents");
  assert.equal(waiting.more,false);
  assert.match(await refused(mf,decidingToken,"approve_action",{actionId:queued.actionId,planDigest:"A".repeat(43)}),/plan-changed/);
  assert.equal((await mcp(mf,otherToken,"approve_action",{actionId:queued.actionId,planDigest:plan.planDigest})).status,403,"an agent's own grant never approves");
  assert.deepEqual(await call(mf,decidingToken,"approve_action",{actionId:queued.actionId,planDigest:plan.planDigest}),{actionId:queued.actionId,status:"approved"});
  assert.deepEqual(await db.prepare("SELECT decision,grant_id FROM owner_grant_decisions WHERE action_id=?").bind(queued.actionId).all().then(r=>r.results),
    [{decision:"approved",grant_id:live.results[0]!.grant_id}]);
  assert.equal((await db.prepare("SELECT decided_by FROM external_actions WHERE action_id=?").bind(queued.actionId).first<{decided_by:string}>())!.decided_by,grant.human_id);
  assert.equal((await call(mf,decidingToken,"execute_external_action",{actionId:queued.actionId},created.accountId)).status,"completed");
  assert.equal(providerPosts.length,1,"sent once, by the agent's execute");
  // A1-3: the owner's own page for that action, served across both Workers, says which app made the decision.
  const decided=await (await mf.dispatchFetch(issuer+"/account/actions/"+queued.actionId,{headers:{Cookie:owner.session}})).text();
  assert.ok(decided.includes("This action was sent.")&&decided.includes("An app made this decision for you: ")&&decided.includes("; client ID <code>"),decided);
  const unshown=await call(mf,decidingToken,"propose_external_action",{credentialRef:"desk",operation:"publish_text",content:"Dinner at six."},created.accountId);
  const unshownPlan=await call(mf,decidingToken,"get_pending_action",{actionId:unshown.actionId});
  const shown=await (await mf.dispatchFetch(issuer+"/account",{headers:{Cookie:owner.session}})).text();
  assert.ok(shown.includes("<strong>It may approve or reject your agents' pending external actions for you.</strong>"),shown);
  // The same decision in /account's recent actions, read across both Workers: that action's entry names the app.
  const entry=shown.split("<li>").find(item=>item.includes('href="/account/actions/'+queued.actionId+'"'));
  assert.ok(entry?.includes(": sent; decided by an app you let decide: ")&&entry.includes(", client ID <code>"),entry);
  const stop=/name="action" value="([^"]+)"><button type="submit">Stop it approving<\/button>/.exec(shown)?.[1];
  assert.ok(stop,"the account page offers to stop it");
  const stopped=await mf.dispatchFetch(issuer+"/account",{method:"POST",redirect:"manual",headers:{"Content-Type":"application/x-www-form-urlencoded",
    Origin:issuer,Cookie:owner.session},body:new URLSearchParams({action:stop}).toString()});
  assert.equal(stopped.status,303,await stopped.clone().text());
  assert.match(stopped.headers.get("location")??"",/result=done/);
  assert.notEqual((await db.prepare("SELECT revoked_at FROM owner_grant_approvals WHERE grant_id=?").bind(live.results[0]!.grant_id).first<{revoked_at:number|null}>())!.revoked_at,null);
  assert.ok(Array.isArray((await call(mf,decidingToken,"list_agents")).items),"stopping it keeps the app's other access");
  assert.match(await refused(mf,decidingToken,"approve_action",{actionId:unshown.actionId,planDigest:unshownPlan.planDigest}),/decisions-off/,"stopped on /account: the next call is refused");
  assert.match(await refused(mf,decidingToken,"reject_action",{actionId:unshown.actionId}),/decisions-off/);
  assert.equal((await db.prepare("SELECT status FROM external_actions WHERE action_id=?").bind(unshown.actionId).first<{status:string}>())!.status,"pending");
  const after=await (await mf.dispatchFetch(issuer+"/account",{headers:{Cookie:owner.session}})).text();
  assert.ok(after.includes("It cannot approve or reject your agents' external actions")&&!after.includes("Stop it approving"),after);
});
