import assert from "node:assert/strict";
import {createHash,randomBytes,randomUUID} from "node:crypto";
import {readFileSync} from "node:fs";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {after,before,test} from "node:test";
import {Miniflare} from "miniflare";
import {stripTypeScriptTypes} from "node:module";
import {initializeDatabase} from "../../mailbox-worker/scripts/latest-schema.ts";
import {demoOAuthClientId,demoOAuthClientMetadata} from "../../mailbox-worker/src/oauth-client-metadata.ts";
import {signedCookieValue} from "../src/human-session.ts";

const issuer="https://auth.weave.example";
const resource="https://demo.weave-389.workers.dev/mcp";
// Exercise the deployed demo's public CIMD contract, not a seeded KV client.
const clientId=demoOAuthClientId;
const redirectUri="http://127.0.0.1:53621/callback";
const redirectUriB="http://127.0.0.1:53622/callback";
const scope="weave:addresses.read weave:addresses.write weave:contacts.read weave:contacts.write weave:messages.read weave:messages.send weave:messages.ack";
const githubClientSecret=randomBytes(20).toString("hex");
const sessionKey=randomBytes(32).toString("base64url");
const bundled=readFileSync(resolve("dist/index.js"),"utf8");
const mailboxBundle=readFileSync(resolve("../mailbox-worker/dist/index.js"),"utf8");
// Execute the real Weave resource authorization module in a separate workerd
// isolate. No mock token validator or canned claims may stand in for the AS.
const resourceAuthorization=stripTypeScriptTypes(
  readFileSync(resolve("../mailbox-worker/src/remote-oauth.ts"),"utf8"),
  {mode:"strip"}
);
const resourceWorker=resourceAuthorization+`
export default {
  async fetch(request,env){
    let oauth;
    try {
      oauth=oauthConfiguration({
        OAUTH_ISSUER:env.OAUTH_ISSUER,
        OAUTH_RESOURCE:env.OAUTH_RESOURCE,
        AUTH_SERVER:env.AUTH_SERVER
      },request.url);
    } catch {return new Response("OAuth unavailable",{status:503});}
    if(!oauth)return new Response("OAuth unavailable",{status:503});
    const access=await authorizeOAuth(oauth,request,"weave:messages.send",
      async owner=>(await env.ACCOUNTS.prepare(
        "SELECT account_id FROM accounts WHERE account_id=?"
      ).bind(owner).first("account_id"))===owner);
    return access instanceof Response?access:Response.json({accountId:access.accountId});
  }
};`;
// Payload generated with the exact mailbox wire shape.
function mailboxPayload(bytes:string,now=Date.now()){
  const hex=now.toString(16).padStart(12,"0");
  return {
    messageId:hex.slice(0,8)+"-"+hex.slice(8)+"-7"+
      randomBytes(2).toString("hex").slice(1)+"-8"+
      randomBytes(2).toString("hex").slice(1)+"-"+
      randomBytes(6).toString("hex"),
    createdAt:new Date(now).toISOString(),
    expiresAt:new Date(now+3600000).toISOString(),
    payloadEncoding:"text/plain",
    bytes:Buffer.from(bytes).toString("base64")
  };
}

// Fake GitHub: the code "gh-<id>" signs in GitHub user <id> as login "human<id>".
function outbound(request:Request):Response|Promise<Response>{
  if(request.method==="GET"&&request.url===clientId)return demoOAuthClientMetadata();
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

const logs:string[]=[];
function options(storage:string,authEnv:Record<string,unknown>={},mailboxEnv:Record<string,unknown>={}) {
  return {
    resourcePersistencePath:storage,
    cf:false,
    handleStructuredLogs:(log:{message:string})=>{logs.push(log.message);},
    workers:[{
      // Fail closed for every destination except the canonical CIMD and the two GitHub endpoints.
      dev:{outboundService:{type:"fetcher",handler:outbound}},
      config:{
        name:"weave-consent-regression",
        // Exact shipped bundle; the locked workerd supports 2026-10-13.
        compatibilityDate:"2026-10-13",
        compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"index.js",modules:{"index.js":{type:"esm",contents:bundled}}},
        env:{
          OAUTH_KV:{type:"kv"},
          CONSENT_NONCES:{type:"durable-object",worker:"weave-consent-regression",exportName:"ConsentNonce"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          GITHUB_CLIENT_ID:{type:"text",value:"Iv1.test-client-id"},
          GITHUB_CLIENT_SECRET:{type:"text",value:githubClientSecret},
          HUMAN_SESSION_KEY:{type:"text",value:sessionKey},
          MAILBOX:{type:"worker",worker:"weave-mailbox-regression",exportName:"HumanDirectory"},
          ...authEnv
        },
        exports:{ConsentNonce:{type:"durable-object",storage:"sqlite"}}
      }
    },{
      config:{
        name:"weave-resource-regression",
        compatibilityDate:"2026-10-13",
        compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"resource.js",modules:{"resource.js":{type:"esm",contents:resourceWorker}}},
        env:{
          AUTH_SERVER:{type:"worker",worker:"weave-consent-regression"},
          ACCOUNTS:{type:"d1"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource}
        }
      }
    },{
      config:{
        name:"weave-mailbox-regression",
        compatibilityDate:"2026-10-13",
        compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"mailbox.js",modules:{"mailbox.js":{type:"esm",contents:mailboxBundle}}},
        env:{
          AUTH_SERVER:{type:"worker",worker:"weave-consent-regression"},
          db:{type:"d1"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          REGISTRATION_ENABLED:{type:"text",value:"true"},
          REGISTRATION_MAX_ACCOUNTS:{type:"text",value:"1000"},
          REGISTRATION_PER_IP_PER_HOUR:{type:"text",value:"1000"},
          REGISTRATION_GLOBAL_PER_HOUR:{type:"text",value:"1000"},
          CLEANUP_MAX_MESSAGES:{type:"text",value:"0"},
          CLEANUP_TIME_BUDGET_MS:{type:"text",value:"0"},
          ...mailboxEnv
        }
      }
    }]
  };
}

type Instance={mf:Miniflare;storage:string};
async function start(authEnv?:Record<string,unknown>,mailboxEnv?:Record<string,unknown>):Promise<Instance>{
  const storage=await mkdtemp(join(tmpdir(),"weave-auth-consent-"));
  const mf=new Miniflare(options(storage,authEnv,mailboxEnv));
  await initializeDatabase(await mf.getD1Database("db","weave-mailbox-regression"),BigInt(Date.now())*10000n);
  await (await mf.getD1Database("ACCOUNTS","weave-resource-regression"))
    .exec("CREATE TABLE accounts (account_id TEXT PRIMARY KEY);");
  return {mf,storage};
}
async function stop(instance:Instance){
  await instance.mf.dispose();
  await rm(instance.storage,{recursive:true,force:true});
}

type Authorization={url:URL;verifier:string;redirectUri:string;state:string};
function authorization(redirect=redirectUri):Authorization{
  const verifier=randomBytes(32).toString("base64url"),state="state-"+randomUUID();
  const url=new URL(issuer+"/authorize");
  for(const [k,v] of Object.entries({
    response_type:"code",client_id:clientId,redirect_uri:redirect,scope,state,resource,
    code_challenge:createHash("sha256").update(verifier).digest("base64url"),code_challenge_method:"S256"
  }))url.searchParams.set(k,v);
  return {url,verifier,redirectUri:redirect,state};
}
const cookiePairs=(headers:{getSetCookie():string[]},prefix:string)=>
  headers.getSetCookie().filter(x=>x.startsWith(prefix)).map(x=>x.split(";")[0]);
/** No session: GET /authorize redirects to GitHub, and the callback returns to that exact URL with a session. */
async function signIn(mf:Miniflare,auth:Authorization,githubId:number):Promise<string>{
  const begin=await mf.dispatchFetch(auth.url.href,{redirect:"manual"});
  assert.equal(begin.status,302,"no human session must start GitHub sign-in");
  const github=new URL(begin.headers.get("location")!);
  assert.equal(github.origin+github.pathname,"https://github.com/login/oauth/authorize");
  const [login]=cookiePairs(begin.headers,"__Host-weave_login=");
  assert.ok(login);
  const back=await mf.dispatchFetch(issuer+"/login/github/callback?"+new URLSearchParams(
    {code:"gh-"+githubId,state:github.searchParams.get("state")!}),{redirect:"manual",headers:{Cookie:login}});
  assert.equal(back.status,302);
  assert.equal(back.headers.get("location"),auth.url.href,"sign-in must return to the exact authorization request");
  const [session]=cookiePairs(back.headers,"__Host-weave_human=");
  assert.ok(session);
  return session;
}
type Consent={handle:string;cookie:string;html:string};
async function openConsent(mf:Miniflare,auth:Authorization,session:string):Promise<Consent>{
  const page=await mf.dispatchFetch(auth.url.href,{redirect:"manual",headers:{Cookie:session}});
  assert.equal(page.status,200,"a signed-in human must see the consent page");
  const html=await page.text();
  const handle=html.match(/name="handle" value="([A-Za-z0-9_-]{43})"/)?.[1];
  assert.ok(handle,"SDK-generated browser-bound consent handle must be rendered");
  const oauth=page.headers.getSetCookie().map(x=>x.split(";")[0]);
  assert.ok(oauth.some(x=>x.includes("oauth")),"SDK browser-bound consent cookie must be set");
  return {handle,cookie:[session,...oauth].join("; "),html};
}
async function post(mf:Miniflare,cookie:string|null,fields:Record<string,string>){
  return mf.dispatchFetch(issuer+"/authorize",{method:"POST",redirect:"manual",
    headers:{Origin:issuer,"Content-Type":"application/x-www-form-urlencoded",...cookie?{Cookie:cookie}:{}},
    body:new URLSearchParams(fields).toString()});
}
function codeFrom(response:{status:number;headers:{get(name:string):string|null}},auth:Authorization):string{
  assert.equal(response.status,302);
  const redirect=new URL(response.headers.get("location")!);
  assert.equal(redirect.origin+redirect.pathname,auth.redirectUri);
  assert.equal(redirect.searchParams.get("state"),auth.state);
  assert.equal(redirect.searchParams.get("iss"),issuer);
  const code=redirect.searchParams.get("code");
  assert.ok(code,"authorization code must be returned after explicit consent");
  return code;
}
async function exchange(mf:Miniflare,code:string,auth:Authorization){
  // Discover the SDK's actual token endpoint; v1.2.4 defaults to /oauth/token.
  const metadata=await mf.dispatchFetch(issuer+"/.well-known/oauth-authorization-server");
  const {token_endpoint:tokenEndpoint}=await metadata.json() as {token_endpoint:string};
  assert.equal(new URL(tokenEndpoint).origin,issuer);
  const token=await mf.dispatchFetch(tokenEndpoint,{method:"POST",redirect:"manual",
    headers:{"Content-Type":"application/x-www-form-urlencoded"},
    body:new URLSearchParams({grant_type:"authorization_code",code,client_id:clientId,
      redirect_uri:auth.redirectUri,code_verifier:auth.verifier,resource}).toString()});
  assert.equal(token.status,200,"actual AS must exchange a PKCE code for resource-bound token");
  const grant=await token.json() as {access_token?:string;expires_in?:number;resource?:string};
  assert.ok(grant.access_token);
  assert.equal(grant.expires_in,900,"resource's 15-minute bound must not be relaxed");
  assert.equal(grant.resource,resource);
  return grant.access_token;
}
/** Full sign-in, consent and PKCE exchange; returns the token and the account it was granted for. */
async function authorizeAgent(mf:Miniflare,githubId:number,agent:string,agentName?:string,session?:string){
  const auth=authorization();
  session??=await signIn(mf,auth,githubId);
  const consent=await openConsent(mf,auth,session);
  const fields:Record<string,string>={handle:consent.handle,decision:"approve",agent};
  if(agentName!==undefined)fields.agentName=agentName;
  const token=await exchange(mf,codeFrom(await post(mf,consent.cookie,fields),auth),auth);
  return {token,session,accountId:await profileId(mf,token)};
}
let rpcId=100;
async function privateCall(mf:Miniflare,token:string,name:string,args:Record<string,unknown>={}):Promise<any>{
  const mailbox=await mf.getWorker("weave-mailbox-regression");
  const response=await mailbox.fetch(resource,{
    method:"POST",headers:{"Content-Type":"application/json",
      Accept:"application/json, text/event-stream",
      "MCP-Protocol-Version":"2025-11-25",Authorization:"Bearer "+token},
    body:JSON.stringify({jsonrpc:"2.0",id:rpcId++,method:"tools/call",params:{name,arguments:args}})
  });
  assert.equal(response.status,200,"real MCP rejected "+name);
  const frame=await response.json() as {result?:{isError?:boolean;structuredContent?:unknown;content?:{text:string}[]}};
  assert.ok(frame.result&&!frame.result.isError,"MCP private tool failed "+name);
  return frame.result.structuredContent??JSON.parse(frame.result.content![0].text);
}
async function profileId(mf:Miniflare,token:string):Promise<string>{
  return (await privateCall(mf,token,"get_my_profile") as {id:string}).id;
}
async function mailboxDb(mf:Miniflare){return mf.getD1Database("db","weave-mailbox-regression");}
async function humanIdOf(mf:Miniflare,githubId:number):Promise<string>{
  const id=await (await mailboxDb(mf)).prepare("SELECT human_id FROM humans WHERE subject=?").bind(String(githubId)).first("human_id");
  assert.equal(typeof id,"string");
  return id as string;
}
async function agentsOf(mf:Miniflare,humanId:string):Promise<string[]>{
  const rows=await (await mailboxDb(mf)).prepare("SELECT account_id FROM accounts WHERE human_id=? ORDER BY account_id")
    .bind(humanId).all<{account_id:string}>();
  return rows.results.map(x=>x.account_id);
}
async function identify(mf:Miniflare,token:string){
  const worker=await mf.getWorker("weave-resource-regression");
  return worker.fetch(resource,{method:"POST",headers:{Authorization:"Bearer "+token}});
}
async function registerAtResource(mf:Miniflare,accountId:string){
  await (await mf.getD1Database("ACCOUNTS","weave-resource-regression"))
    .prepare("INSERT INTO accounts (account_id) VALUES (?)").bind(accountId).run();
}
async function nonceStatus(mf:Miniflare,handle:string):Promise<number>{
  const ns=await mf.getDurableObjectNamespace("CONSENT_NONCES");
  return (await ns.get(ns.idFromName(handle)).fetch("https://nonce.internal/nonce")).status;
}
async function grantsFor(mf:Miniflare,accountId:string):Promise<number>{
  return (await (await mf.getKVNamespace("OAUTH_KV")).list({prefix:"grant:"+accountId+":"})).keys.length;
}

let shared:Instance;
before(async()=>{shared=await start();},{timeout:60_000});
after(async()=>{await stop(shared);});

test("consent_NewAgent_IssuesTokenForThatAccount",{timeout:60_000},async()=>{
  const {mf}=shared;
  const auth=authorization();
  const session=await signIn(mf,auth,1001);
  const consent=await openConsent(mf,auth,session);
  assert.match(consent.html,/Signed in as @human1001/);
  const token=await exchange(mf,codeFrom(await post(mf,consent.cookie,
    {handle:consent.handle,decision:"approve",agent:"new",agentName:"Claude Code"}),auth),auth);
  const humanId=await humanIdOf(mf,1001);
  const owned=await agentsOf(mf,humanId);
  assert.equal(owned.length,1,"exactly one agent is created for the signed-in human");
  const [accountId]=owned;
  const row=await (await mailboxDb(mf)).prepare("SELECT human_id,display_name,control_hash FROM accounts WHERE account_id=?")
    .bind(accountId).first<{human_id:string;display_name:string;control_hash:string|null}>();
  assert.deepEqual(row,{human_id:humanId,display_name:"Claude Code",control_hash:null});
  assert.equal(await grantsFor(mf,accountId),1,"grant must be attributed to the created account UUID");
  await registerAtResource(mf,accountId);
  const owner=await identify(mf,token);
  assert.equal(owner.status,200,"real resource must accept the AS-issued token");
  assert.deepEqual(await owner.json(),{accountId});
});

test("consent_SecondClientPicksExistingAgent_SameAccountId",{timeout:60_000},async()=>{
  const {mf}=shared;
  const first=await authorizeAgent(mf,2001,"new","First client");
  // The same signed-in browser authorizes a second client without signing in again.
  const auth=authorization(redirectUriB);
  const consent=await openConsent(mf,auth,first.session);
  assert.match(consent.html,new RegExp('name="agent" value="'+first.accountId+'"'));
  assert.match(consent.html,/First client/);
  const token=await exchange(mf,codeFrom(await post(mf,consent.cookie,
    {handle:consent.handle,decision:"approve",agent:first.accountId}),auth),auth);
  assert.notEqual(token,first.token);
  assert.equal(await profileId(mf,token),first.accountId);
  assert.deepEqual(await agentsOf(mf,await humanIdOf(mf,2001)),[first.accountId],"picking an agent must not create one");
  assert.equal(await grantsFor(mf,first.accountId),2);
});

test("consent_OtherHumansAgentId_DeniedWithoutCode",{timeout:60_000},async()=>{
  const {mf}=shared;
  const victim=await authorizeAgent(mf,3001,"new","Victim agent");
  const auth=authorization();
  const consent=await openConsent(mf,auth,await signIn(mf,auth,3002));
  assert.doesNotMatch(consent.html,new RegExp(victim.accountId),"another human's agents are never listed");
  const response=await post(mf,consent.cookie,{handle:consent.handle,decision:"approve",agent:victim.accountId});
  assert.equal(response.status,400);
  assert.equal(response.headers.get("location"),null);
  assert.equal(await grantsFor(mf,victim.accountId),1,"no grant for a foreign agent");
  assert.deepEqual(await agentsOf(mf,await humanIdOf(mf,3002)),[]);
});

test("consent_EleventhNewAgent_ShowsLimitWithoutCode",{timeout:120_000},async()=>{
  const {mf}=shared;
  const auth=authorization();
  const session=await signIn(mf,auth,4001);
  for(let i=1;i<=10;i++){
    const next=authorization();
    const consent=await openConsent(mf,next,session);
    codeFrom(await post(mf,consent.cookie,{handle:consent.handle,decision:"approve",agent:"new",agentName:"Agent "+i}),next);
  }
  const humanId=await humanIdOf(mf,4001);
  assert.equal((await agentsOf(mf,humanId)).length,10);
  const consent=await openConsent(mf,auth,session);
  const response=await post(mf,consent.cookie,{handle:consent.handle,decision:"approve",agent:"new",agentName:"Agent 11"});
  assert.equal(response.status,400);
  assert.equal(response.headers.get("location"),null);
  assert.match(await response.text(),/already has the maximum of 10 agents/);
  assert.equal((await agentsOf(mf,humanId)).length,10);
});

test("consent_ExpiredOrTamperedSession_RedirectsToGithubWithoutCode",{timeout:60_000},async()=>{
  const {mf}=shared;
  const humanId=randomUUID();
  const expired="__Host-weave_human="+await signedCookieValue(sessionKey,"human-session",
    {humanId,login:"human5001",expiresAt:Date.now()-1});
  const valid=await signedCookieValue(sessionKey,"human-session",{humanId,login:"human5001",expiresAt:Date.now()+600_000});
  const [payload,signature]=valid.split(".");
  const forgedPayload=Buffer.from(JSON.stringify({humanId:randomUUID(),login:"human5001",expiresAt:Date.now()+600_000})).toString("base64url");
  const tampered="__Host-weave_human="+forgedPayload+"."+signature;
  assert.notEqual(payload,forgedPayload);
  for(const cookie of [expired,tampered]){
    const auth=authorization();
    const response=await mf.dispatchFetch(auth.url.href,{redirect:"manual",headers:{Cookie:cookie}});
    assert.equal(response.status,302);
    assert.equal(new URL(response.headers.get("location")!).origin,"https://github.com");
    assert.ok(cookiePairs(response.headers,"__Host-weave_login=").length===1);
  }

  // The session expires between the consent page and the decision: no code, back to GitHub.
  const auth=authorization();
  const session=await signIn(mf,auth,5002);
  const consent=await openConsent(mf,auth,session);
  const withoutSession=consent.cookie.split("; ").filter(x=>!x.startsWith("__Host-weave_human=")).concat(tampered).join("; ");
  const response=await post(mf,withoutSession,{handle:consent.handle,decision:"approve",agent:"new",agentName:"Late"});
  assert.equal(response.status,302);
  const github=new URL(response.headers.get("location")!);
  assert.equal(github.origin+github.pathname,"https://github.com/login/oauth/authorize");
  assert.equal(await nonceStatus(mf,consent.handle),200,"no nonce is spent without a human session");
  assert.deepEqual(await agentsOf(mf,await humanIdOf(mf,5002)),[]);
  // Signing in again resumes a fresh /authorize request for the same client and PKCE challenge.
  const [login]=cookiePairs(response.headers,"__Host-weave_login=");
  const back=await mf.dispatchFetch(issuer+"/login/github/callback?"+new URLSearchParams(
    {code:"gh-5002",state:github.searchParams.get("state")!}),{redirect:"manual",headers:{Cookie:login}});
  assert.equal(back.status,302);
  const resumed=new URL(back.headers.get("location")!);
  assert.equal(resumed.origin+resumed.pathname,issuer+"/authorize");
  for(const name of ["response_type","client_id","redirect_uri","scope","state","resource","code_challenge","code_challenge_method"])
    assert.equal(resumed.searchParams.get(name),auth.url.searchParams.get(name),name);
  const [renewed]=cookiePairs(back.headers,"__Host-weave_human=");
  const page=await mf.dispatchFetch(resumed.href,{redirect:"manual",headers:{Cookie:renewed}});
  assert.equal(page.status,200);
});

test("consent_Deny_RedirectsWithAccessDenied",{timeout:60_000},async()=>{
  const {mf}=shared;
  const auth=authorization();
  const consent=await openConsent(mf,auth,await signIn(mf,auth,6001));
  const response=await post(mf,consent.cookie,{handle:consent.handle,decision:"deny"});
  assert.equal(response.status,302);
  const redirect=new URL(response.headers.get("location")!);
  assert.equal(redirect.origin+redirect.pathname,redirectUri);
  assert.equal(redirect.searchParams.get("error"),"access_denied");
  assert.equal(redirect.searchParams.get("state"),auth.state);
  assert.equal(redirect.searchParams.get("code"),null);
  assert.deepEqual(await agentsOf(mf,await humanIdOf(mf,6001)),[]);
});

test("consent_RateLimitedNewAgent_Returns429WithoutCode",{timeout:60_000},async()=>{
  const instance=await start({},{REGISTRATION_GLOBAL_PER_HOUR:{type:"text",value:"1"}});
  try{
    const {mf}=instance;
    const first=await authorizeAgent(mf,7001,"new","Only one");
    const auth=authorization();
    const consent=await openConsent(mf,auth,first.session);
    const response=await post(mf,consent.cookie,{handle:consent.handle,decision:"approve",agent:"new",agentName:"Second"});
    assert.equal(response.status,429);
    assert.equal(response.headers.get("location"),null);
    assert.match(await response.text(),/Too many new agents/);
  }finally{await stop(instance);}
});

test("consent_DirectoryUnavailable_Returns503AndLogsCategory",{timeout:60_000},async()=>{
  // The default export has no directory methods, so every directory RPC fails.
  const instance=await start({MAILBOX:{type:"worker",worker:"weave-mailbox-regression"}});
  logs.length=0;
  try{
    const session="__Host-weave_human="+await signedCookieValue(sessionKey,"human-session",
      {humanId:randomUUID(),login:"human8001",expiresAt:Date.now()+600_000});
    const auth=authorization();
    const response=await instance.mf.dispatchFetch(auth.url.href,{redirect:"manual",headers:{Cookie:session}});
    assert.equal(response.status,503);
    assert.ok(logs.some(x=>x.includes("human-directory: call failed")),JSON.stringify(logs));
  }finally{await stop(instance);}
});

test("a foreign browser cannot burn an owner nonce before consent-cookie verification",{timeout:120_000},async()=>{
  let instance=await start();
  try{
    let {mf}=instance;
    const auth=authorization();
    auth.url.searchParams.set("state","separate-browser-state");auth.state="separate-browser-state";
    const session=await signIn(mf,auth,9001);
    const consent=await openConsent(mf,auth,session);
    const fields={handle:consent.handle,decision:"approve",agent:"new",agentName:"Agent A"};

    // A stolen handle, even with the attacker's own signed-in session, must NOT
    // consume the owner's one-time nonce without the SDK's browser-binding cookie.
    const attackerAuth=authorization();
    const attacker=await signIn(mf,attackerAuth,9002);
    for(const cookie of [null,attacker]){
      const foreign=await post(mf,cookie,fields);
      assert.equal(foreign.status,400);
      assert.equal(await nonceStatus(mf,consent.handle),200,"unauthorized browser must not destroy valid owner's nonce");
    }
    assert.deepEqual(await agentsOf(mf,await humanIdOf(mf,9002)),[]);

    const allowed=await post(mf,consent.cookie,fields);
    const token=await exchange(mf,codeFrom(allowed,auth),auth);
    const account=await profileId(mf,token);
    assert.deepEqual(await agentsOf(mf,await humanIdOf(mf,9001)),[account]);
    assert.equal(await grantsFor(mf,account),1);
    const replay=await post(mf,consent.cookie,fields);
    assert.equal(replay.status,400,"SDK handle + Durable Object nonce can never grant twice");
    assert.deepEqual(await agentsOf(mf,await humanIdOf(mf,9001)),[account],"a replay must not create another agent");

    // A second human independently consents with a separate grant.
    const second=await authorizeAgent(mf,9003,"new","Agent B");
    const accountB=second.accountId,tokenB=second.token;
    assert.notEqual(accountB,account);
    assert.notEqual(tokenB,token);
    assert.equal(await grantsFor(mf,accountB),1);
    assert.equal(await grantsFor(mf,account),1,"authorizing a different account must not replace the first account's grant");

    // Full OAuth -> actual /mcp tools/call -> production account-scoped D1.
    assert.deepEqual(await privateCall(mf,token,"get_my_profile"),{id:account});
    assert.deepEqual(await privateCall(mf,tokenB,"get_my_profile"),{id:accountB});
    const aAddress=randomUUID(),bAddress=randomUUID();
    const addressA=await privateCall(mf,token,"ensure_my_address",{addressId:aAddress,label:"Test Agent A"});
    const addressB=await privateCall(mf,tokenB,"ensure_my_address",{addressId:bAddress,label:"Test Agent B"});
    assert.equal(addressA.isPublic,false);
    assert.equal(addressB.isPublic,false);
    const addressesA=await privateCall(mf,token,"list_my_addresses");
    assert.ok(addressesA.items.some((x:{addressId:string})=>x.addressId===aAddress));
    assert.ok(!addressesA.items.some((x:{addressId:string})=>x.addressId===bAddress),
      "account A must not read private B addresses");
    const intro=mailboxPayload("test-consent-introduction");
    const requested=await privateCall(mf,token,"request_contact",{
      requesterAddressId:aAddress,recipientAccountId:accountB,recipientAddressId:bAddress,payload:intro});
    const pending=await privateCall(mf,tokenB,"list_my_contact_requests");
    assert.ok(pending.items.some((x:{requestId:string;status:string})=>
      x.requestId===intro.messageId&&x.status==="pending"));
    await privateCall(mf,tokenB,"decide_contact",{
      requestId:intro.messageId,requesterAccountId:account,
      expectedGeneration:String(requested.generation),status:"accepted"});
    const deliveries:{token:string;receiverToken:string;sender:string;recipient:string;
      payload:ReturnType<typeof mailboxPayload>}[]=[];
    async function sendMessage(index:number):Promise<void>{
      const fromA=index%2===0;
      const sender=fromA?account:accountB,recipient=fromA?accountB:account;
      const from=fromA?token:tokenB,to=fromA?tokenB:token;
      const payload=mailboxPayload("test-message-"+index);
      const receipt=await privateCall(mf,from,"send_message",{
        recipientAccountId:recipient,contactGeneration:String(requested.generation),payload});
      assert.equal(receipt.messageId,payload.messageId);
      assert.equal(receipt.state,"pending");
      deliveries.push({token:from,receiverToken:to,sender,recipient,payload});
    }
    // Leave one message in EACH direction pending across a genuine cold restart.
    await sendMessage(0);
    await sendMessage(1);
    await mf.dispose();
    mf=new Miniflare(options(instance.storage));
    instance={mf,storage:instance.storage};
    async function acknowledge(item:(typeof deliveries)[number]):Promise<void>{
      const inbox=await privateCall(mf,item.receiverToken,"list_my_inbox");
      assert.ok(inbox.items.some((x:{envelope?:{payload?:{messageId:string}}})=>
        x.envelope?.payload?.messageId===item.payload.messageId),
        "inbox must retain original pending message after Worker restart");
      const receipt=await privateCall(mf,item.receiverToken,"ack_message",{
        messageId:item.payload.messageId,senderAccountId:item.sender});
      assert.equal(receipt.state,"acknowledged");
    }
    await acknowledge(deliveries[0]);
    await acknowledge(deliveries[1]);
    for(let i=2;i<6;i++){
      await sendMessage(i);
      await acknowledge(deliveries[i]);
    }
    const replayedSend=await privateCall(mf,deliveries[0].token,"send_message",{
      recipientAccountId:deliveries[0].recipient,
      contactGeneration:String(requested.generation),payload:deliveries[0].payload});
    assert.equal(replayedSend.state,"acknowledged","identical replay returns original ACK");
    const outA=await privateCall(mf,token,"list_my_outbox");
    const outB=await privateCall(mf,tokenB,"list_my_outbox");
    assert.ok(outA.items.length>=3&&outB.items.length>=3,"both accounts retain three outgoing receipts");

    // Real two-Worker Service Binding, real AS KV tokens, and the real
    // resource's authorizeOAuth source running in workerd against D1.
    await registerAtResource(mf,account);
    await registerAtResource(mf,accountB);
    const ownerA=await identify(mf,token);
    assert.equal(ownerA.status,200,"real resource must accept AS-verified account A");
    assert.deepEqual(await ownerA.json(),{accountId:account});
    const ownerB=await identify(mf,tokenB);
    assert.equal(ownerB.status,200,"a second token must never bind to account A");
    assert.deepEqual(await ownerB.json(),{accountId:accountB});
    assert.equal((await identify(mf,"not-a-valid-cloudflare-access-token")).status,401);
    await (await mf.getD1Database("ACCOUNTS","weave-resource-regression"))
      .prepare("DELETE FROM accounts WHERE account_id=?").bind(accountB).run();
    assert.equal((await identify(mf,tokenB)).status,401,
      "removing a Weave account must immediately revoke private resource access");
    assert.equal((await identify(mf,token)).status,200,
      "removing B must not deny the independently owned account A");
    const revocationMetadata=await mf.dispatchFetch(issuer+"/.well-known/oauth-authorization-server");
    const {revocation_endpoint:revocationEndpoint}=await revocationMetadata.json() as {revocation_endpoint:string};
    assert.equal(new URL(revocationEndpoint).origin,issuer);
    const revoked=await mf.dispatchFetch(revocationEndpoint,{method:"POST",redirect:"manual",
      headers:{"Content-Type":"application/x-www-form-urlencoded"},
      body:new URLSearchParams({token,client_id:clientId}).toString()});
    assert.equal(revoked.status,200,"the real AS must accept its own token revocation");
    assert.equal((await identify(mf,token)).status,401,
      "token revocation must be observed at the next Service Binding lookup");
  }finally{await stop(instance);}
});

test("consent_HumanFrozenAfterSignIn_NoCodeForExistingOrNewAgent",{timeout:60_000},async()=>{
  const {mf}=shared;
  const owner=await authorizeAgent(mf,10001,"new","Before freeze");
  const humanId=await humanIdOf(mf,10001);
  await (await mailboxDb(mf)).prepare("UPDATE humans SET frozen_at=updated_at WHERE human_id=?").bind(humanId).run();
  for(const choice of [{agent:owner.accountId},{agent:"new",agentName:"After freeze"}] as Record<string,string>[]){
    const auth=authorization();
    const consent=await openConsent(mf,auth,owner.session);
    assert.doesNotMatch(consent.html,new RegExp(owner.accountId),"a frozen human's agents are not offered");
    const response=await post(mf,consent.cookie,{handle:consent.handle,decision:"approve",...choice});
    assert.equal(response.status,choice.agent==="new"?403:400);
    assert.equal(response.headers.get("location"),null,"no code for a frozen human");
    if(choice.agent==="new")assert.match(await response.text(),/This GitHub identity is frozen; contact the operator/);
  }
  assert.equal(await grantsFor(mf,owner.accountId),1,"no grant after the freeze");
  assert.deepEqual(await agentsOf(mf,humanId),[owner.accountId],"no agent created after the freeze");
});

test("consent_AgentCreationUnavailable_Returns503WithoutCode",{timeout:60_000},async()=>{
  const instance=await start({},{REGISTRATION_ENABLED:{type:"text",value:"false"}});
  try{
    const {mf}=instance;
    const auth=authorization();
    const consent=await openConsent(mf,auth,await signIn(mf,auth,11001));
    const response=await post(mf,consent.cookie,{handle:consent.handle,decision:"approve",agent:"new",agentName:"Claude Code"});
    assert.equal(response.status,503);
    assert.equal(response.headers.get("location"),null);
    assert.match(await response.text(),/New agents cannot be created right now/);
    assert.deepEqual(await agentsOf(mf,await humanIdOf(mf,11001)),[]);
  }finally{await stop(instance);}
});
