import assert from "node:assert/strict";
import {createHash,randomBytes,randomUUID} from "node:crypto";
import {readFileSync} from "node:fs";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {after,before,test} from "node:test";
import {Miniflare} from "miniflare";
import {initializeDatabase} from "../../mailbox-worker/scripts/latest-schema.ts";
import {demoOAuthClientId,demoOAuthClientMetadata} from "../../mailbox-worker/src/oauth-client-metadata.ts";
import {agentKey,bindingRequest,clientAssertion,dpopProof,keyEndorsement,keyProof,type TestKey} from "../../mailbox-worker/test/agent-key.ts";
import {beginGithubLogin} from "../src/github-login.ts";
import {signedCookieValue} from "../src/human-session.ts";
import {ownerActionToken} from "../src/owner-forms.ts";

const issuer="https://auth.weave.example";
const mailboxOrigin="https://mailbox.example";
const resource="https://demo.weave-389.workers.dev/mcp";
const githubClientSecret=randomBytes(20).toString("hex");
const sessionKey=randomBytes(32).toString("base64url");
const bundled=readFileSync(resolve("dist/index.js"),"utf8");
const mailboxBundle=readFileSync(resolve("../mailbox-worker/dist/index.js"),"utf8");
const authName="weave-auth-owner",mailboxName="weave-mailbox-owner";

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

// Fake external action provider for the mailbox worker: completes every post and records it.
const providerEndpoint="https://provider.example/post";
const providerCalls:{actionId:string;content:string;targetAccount:string}[]=[];
// Fake Google for connecting YouTube: the code "yt-<n>" yields a token whose channel is "UC" + 22 copies of n.
const youtubeScope="https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";
async function fakeGoogle(request:Request):Promise<Response|null>{
  if(request.method==="POST"&&request.url==="https://oauth2.googleapis.com/token"){
    const form=new URLSearchParams(await request.text());
    const n=/^yt-([0-9])$/.exec(form.get("code")??"")?.[1];
    if(form.get("client_secret")!=="google-test-secret"||!n||!form.get("code_verifier"))return Response.json({error:"invalid_grant"},{status:400});
    return Response.json({access_token:"ya29.test-"+n,refresh_token:"1//refresh-"+n,scope:youtubeScope,token_type:"Bearer"});
  }
  if(request.method==="GET"&&request.url.startsWith("https://www.googleapis.com/youtube/v3/channels?")){
    const n=/^Bearer ya29\.test-([0-9])$/.exec(request.headers.get("authorization")??"")?.[1];
    return n?Response.json({items:[{id:"UC"+n.repeat(22),snippet:{title:"Channel "+n}}]}):new Response("{}",{status:401,headers:{"Content-Type":"application/json"}});
  }
  return null;
}
const postizKey="postiz-test-key-0123456789";
function fakePostiz(request:Request):Response|null{
  if(request.method!=="GET"||request.url!=="https://api.postiz.com/public/v1/integrations")return null;
  if(request.headers.get("authorization")!==postizKey)return Response.json({message:"Unauthorized"},{status:401});
  return Response.json([{id:"cm4tiktok00000000000000001",name:"HSC Beats",identifier:"tiktok",profile:"hscbeats",disabled:false}]);
}
const blueskyHandle="hscbeats.bsky.social",blueskyPassword="abcd-efgh-ijkl-mnop",blueskyDid="did:plc:"+"a".repeat(24);
async function fakeBluesky(request:Request):Promise<Response|null>{
  if(request.method!=="POST"||request.url!=="https://bsky.social/xrpc/com.atproto.server.createSession")return null;
  const {identifier,password}=await request.json() as {identifier:string;password:string};
  return identifier===blueskyHandle&&password===blueskyPassword?Response.json({did:blueskyDid,handle:blueskyHandle,accessJwt:"jwt",active:true})
    :Response.json({error:"AuthenticationRequired"},{status:401});
}
// Fake LinkedIn userinfo: LinkedIn plans tokens of at least 1000 characters, so this one is that long.
const linkedinToken="AQV"+"a1B2-c3_D4.e5~".repeat(80).slice(0,997),linkedinMember="xY12_ab-CD";
function fakeLinkedIn(request:Request):Response|null{
  if(request.method!=="GET"||request.url!=="https://api.linkedin.com/v2/userinfo")return null;
  return request.headers.get("authorization")==="Bearer "+linkedinToken?Response.json({sub:linkedinMember,name:"HSC Beats"}):Response.json({status:401},{status:401});
}
async function mailboxOutbound(request:Request):Promise<Response>{
  const linkedin=fakeLinkedIn(request);
  if(linkedin)return linkedin;
  const google=await fakeGoogle(request);
  if(google)return google;
  const postiz=fakePostiz(request);
  if(postiz)return postiz;
  const bluesky=await fakeBluesky(request);
  if(bluesky)return bluesky;
  if(request.method!=="POST"||request.url!==providerEndpoint)return new Response("Outbound denied",{status:403});
  const call=await request.json() as {actionId:string;content:string;targetAccount:string};
  providerCalls.push(call);
  return Response.json({externalId:"post-"+call.actionId});
}

const logs:string[]=[];
function options(storage:string,authEnv:Record<string,unknown>={}){
  return {
    resourcePersistencePath:storage,cf:false,
    handleStructuredLogs:(log:{message:string})=>{logs.push(log.message);},
    workers:[{
      dev:{outboundService:{type:"fetcher",handler:outbound}},
      config:{
        name:authName,compatibilityDate:"2026-10-13",compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"index.js",modules:{"index.js":{type:"esm",contents:bundled}}},
        env:{
          OAUTH_KV:{type:"kv"},
          CONSENT_NONCES:{type:"durable-object",worker:authName,exportName:"ConsentNonce"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          GITHUB_CLIENT_ID:{type:"text",value:"Iv1.test-client-id"},
          GITHUB_CLIENT_SECRET:{type:"text",value:githubClientSecret},
          HUMAN_SESSION_KEY:{type:"text",value:sessionKey},
          GOOGLE_CLIENT_ID:{type:"text",value:"weave-test.apps.googleusercontent.com"},
          MAILBOX:{type:"worker",worker:mailboxName,exportName:"HumanDirectory"},
          ...authEnv
        },
        exports:{ConsentNonce:{type:"durable-object",storage:"sqlite"}}
      }
    },{
      dev:{outboundService:{type:"fetcher",handler:mailboxOutbound}},
      config:{
        name:mailboxName,compatibilityDate:"2026-10-13",compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"mailbox.js",modules:{"mailbox.js":{type:"esm",contents:mailboxBundle}}},
        env:{
          db:{type:"d1"},
          EXTERNAL_MEDIA:{type:"r2"},
          REGISTRATION_ENABLED:{type:"text",value:"true"},
          REGISTRATION_MAX_ACCOUNTS:{type:"text",value:"1000"},
          REGISTRATION_PER_IP_PER_HOUR:{type:"text",value:"1000"},
          REGISTRATION_GLOBAL_PER_HOUR:{type:"text",value:"1000"},
          CLEANUP_MAX_MESSAGES:{type:"text",value:"0"},
          CLEANUP_TIME_BUDGET_MS:{type:"text",value:"0"},
          EXTERNAL_ACTION_TEST_ENDPOINT:{type:"text",value:providerEndpoint},
          GOOGLE_CLIENT_ID:{type:"text",value:"weave-test.apps.googleusercontent.com"},
          GOOGLE_CLIENT_SECRET:{type:"text",value:"google-test-secret"},
          EXTERNAL_CREDENTIAL_KEY:{type:"text",value:randomBytes(32).toString("base64url")},
          // First-party OAuth, as deployed: the mailbox then links owners to this authorization server's pages.
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          AUTH_SERVER:{type:"worker",worker:authName}
        }
      }
    }]
  };
}
type Instance={mf:Miniflare;storage:string};
async function start(authEnv?:Record<string,unknown>):Promise<Instance>{
  const storage=await mkdtemp(join(tmpdir(),"weave-auth-owner-"));
  const mf=new Miniflare(options(storage,authEnv));
  await initializeDatabase(await mf.getD1Database("db",mailboxName),BigInt(Date.now())*10000n);
  return {mf,storage};
}
async function stop(instance:Instance){await instance.mf.dispose();await rm(instance.storage,{recursive:true,force:true});}

let shared:Instance;
before(async()=>{shared=await start();},{timeout:60_000});
after(async()=>{await stop(shared);});
const db=()=>shared.mf.getD1Database("db",mailboxName);

// ---- humans, sessions and owner-page requests -------------------------------------------------------------------------
let nextSubject=7000;
type Human={humanId:string;subject:string;login:string};
async function seedHuman(mf:Miniflare=shared.mf):Promise<Human>{
  const subject=String(++nextSubject),humanId=randomUUID(),at=(BigInt(Date.now())*10000n).toString();
  await (await mf.getD1Database("db",mailboxName)).prepare("INSERT INTO humans(human_id,provider,subject,provider_created_at,display_login,created_at,updated_at) VALUES(?,'github',?,'2020-01-01T00:00:00Z',?,CAST(? AS INTEGER),CAST(? AS INTEGER))")
    .bind(humanId,subject,"human"+subject,at,at).run();
  return {humanId,subject,login:"human"+subject};
}
/** A forged session cookie; `authAt` defaults to now (fresh). */
async function sessionCookie(human:Human,authAt=Date.now()):Promise<string>{
  const value=await signedCookieValue(sessionKey,"human-session",{humanId:human.humanId,login:human.login,expiresAt:authAt+3_600_000,authAt});
  return "__Host-weave_human="+value;
}
const account=(cookie?:string,mf:Miniflare=shared.mf)=>mf.dispatchFetch(issuer+"/account",{redirect:"manual",headers:cookie?{Cookie:cookie}:{}});
function postAccount(token:string,cookie:string|null,origin:string|null=issuer,mf:Miniflare=shared.mf){
  return mf.dispatchFetch(issuer+"/account",{method:"POST",redirect:"manual",headers:{
    "Content-Type":"application/x-www-form-urlencoded",...origin?{Origin:origin}:{},...cookie?{Cookie:cookie}:{}},
    body:new URLSearchParams({action:token}).toString()});
}
/** The signed tokens the page renders, by button label, in page order. */
function forms(html:string):{label:string;token:string}[]{
  return [...html.matchAll(/name="action" value="([^"]+)"><button type="submit">([^<]+)<\/button>/g)].map(m=>({token:m[1],label:m[2]}));
}
async function tokenFor(human:Human,label:string,index=0,authAt?:number):Promise<string>{
  const html=await (await account(await sessionCookie(human,authAt))).text();
  const found=forms(html).filter(x=>x.label===label)[index];
  assert.ok(found,"the page must render a "+label+" form");
  return found.token;
}
const result=(response:{headers:{get(name:string):string|null}})=>new URL(response.headers.get("location")!,issuer).searchParams.get("result");

// ---- agents, over the mailbox worker's real HTTP API --------------------------------------------------------------------
type Agent={accountId:string;key:TestKey};
const mailbox=()=>shared.mf.getWorker(mailboxName);
async function agentFetch(agent:Agent,path:string,init:{method?:string;body?:unknown}={}):Promise<Response>{
  const worker=await mailbox(),url=mailboxOrigin+path,method=init.method??"GET";
  const tokenUrl=mailboxOrigin+"/v1/token";
  const form=new URLSearchParams([["grant_type","client_credentials"],["client_assertion_type","urn:ietf:params:oauth:client-assertion-type:jwt-bearer"],
    ["client_assertion",clientAssertion(agent.key,{accountId:agent.accountId,audience:tokenUrl})]]);
  const issued=await worker.fetch(tokenUrl,{method:"POST",headers:{"Content-Type":"application/x-www-form-urlencoded",DPoP:dpopProof(agent.key,{method:"POST",url:tokenUrl})},body:form.toString()});
  assert.equal(issued.status,200,await issued.clone().text());
  const accessToken=(await issued.json() as {access_token:string}).access_token;
  return worker.fetch(url,{method,headers:{"Content-Type":"application/json",Authorization:"DPoP "+accessToken,
    DPoP:dpopProof(agent.key,{method,url,accessToken})},...init.body===undefined?{}:{body:JSON.stringify(init.body)}}) as unknown as Promise<Response>;
}
async function registerAgent(displayName?:string):Promise<Agent>{
  const key=agentKey(),url=mailboxOrigin+"/v1/accounts",worker=await mailbox();
  const response=await worker.fetch(url,{method:"POST",headers:{"Content-Type":"application/json",DPoP:dpopProof(key,{method:"POST",url})},
    body:JSON.stringify(displayName===undefined?{}:{displayName})});
  assert.equal(response.status,200,await response.clone().text());
  return {accountId:(await response.json() as {accountId:string}).accountId,key};
}
type BindingView={requestId:string;status:string};
async function requestBinding(agent:Agent,human:Human):Promise<BindingView>{
  const request=bindingRequest(agent.key,{accountId:agent.accountId,audience:mailboxOrigin+"/v1/binding-requests",human:"github:"+human.subject});
  const response=await agentFetch(agent,"/v1/binding-requests",{method:"POST",body:{request}});
  assert.equal(response.status,200,await response.clone().text());
  return response.json() as Promise<BindingView>;
}
const bindingStatus=async(agent:Agent,requestId:string)=>
  (await (await agentFetch(agent,"/v1/binding-requests/"+requestId)).json() as BindingView).status;
async function ownerOf(agent:Agent):Promise<string|null>{
  return (await (await db()).prepare("SELECT human_id FROM accounts WHERE account_id=?").bind(agent.accountId).first<{human_id:string|null}>())!.human_id;
}
/** An agent registered with a live key, then given to this human directly in storage. */
async function ownedAgent(human:Human,displayName="owned"):Promise<Agent>{
  const agent=await registerAgent(displayName);
  await (await db()).prepare("UPDATE accounts SET human_id=? WHERE account_id=?").bind(human.humanId,agent.accountId).run();
  return agent;
}

type ActionView={actionId:string;status:string;receipt:string|null;approvalUrl:string|null};
async function proposeAction(human:Human,content:string):Promise<{agent:Agent;action:ActionView}>{
  const agent=await ownedAgent(human,"publisher"),at=(BigInt(Date.now())*10000n).toString();
  await (await db()).prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
    VALUES(?,'linkedin-main','test-http','rev-1','urn:li:person:alpha','Desk Alpha',CAST(? AS INTEGER),CAST(? AS INTEGER))`).bind(agent.accountId,at,at).run();
  const response=await agentFetch(agent,"/v1/external-actions",{method:"POST",body:{credentialRef:"linkedin-main",operation:"publish_text",content}});
  assert.equal(response.status,200,await response.clone().text());
  return {agent,action:await response.json() as ActionView};
}
const readAction=async(agent:Agent,actionId:string)=>
  (await agentFetch(agent,"/v1/external-actions/"+actionId)).json() as Promise<ActionView>;
const executeAction=async(agent:Agent,actionId:string)=>
  (await agentFetch(agent,"/v1/external-actions/"+actionId+"/execute",{method:"POST",body:{}})).json() as Promise<ActionView>;

// ---- tests ---------------------------------------------------------------------------------------------------------------
test("account_NoSession_RedirectsToGithubReturningToAccount",{timeout:30_000},async()=>{
  const response=await account();
  assert.equal(response.status,302);
  const location=new URL(response.headers.get("location")!);
  assert.equal(location.origin+location.pathname,"https://github.com/login/oauth/authorize");
  const cookie=response.headers.getSetCookie().find(x=>x.startsWith("__Host-weave_login="))!.split(";")[0].split("=")[1];
  const payload=JSON.parse(Buffer.from(cookie.split(".")[0],"base64url").toString()) as {returnTo:string};
  assert.equal(payload.returnTo,issuer+"/account");
  const other=await shared.mf.dispatchFetch(issuer+"/account",{method:"DELETE",redirect:"manual"});
  assert.equal(other.status,405);
  assert.equal(other.headers.get("allow"),"GET, POST");
});

test("account_PendingBinding_ShowsAgentIdNameAndFingerprints",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await registerAgent("Octo <b>bot</b>");
  await requestBinding(agent,human);
  const response=await account(await sessionCookie(human));
  assert.equal(response.status,200);
  assert.equal(response.headers.get("cache-control"),"no-store");
  assert.equal(response.headers.get("x-frame-options"),"DENY");
  const html=await response.text();
  assert.ok(html.includes("Signed in as @"+human.login));
  assert.ok(html.includes(agent.accountId));
  assert.ok(html.includes("Octo &#60;b&#62;bot&#60;/b&#62;")&&!html.includes("<b>bot"));
  assert.ok(html.includes("self-reported, not verified"));
  assert.ok(html.includes("<strong>"+agent.key.kid.slice(0,8)+"</strong>"+agent.key.kid.slice(8)));
  assert.deepEqual(forms(html).map(x=>x.label),["Confirm binding","Reject"]);
});

test("account_ConfirmBinding_Fresh_303DoneAndAgentOwned",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await registerAgent("a"),view=await requestBinding(agent,human);
  const response=await postAccount(await tokenFor(human,"Confirm binding"),await sessionCookie(human));
  assert.equal(response.status,303);
  assert.equal(response.headers.get("location"),"/account?result=done");
  assert.equal(await ownerOf(agent),human.humanId);
  assert.equal(await bindingStatus(agent,view.requestId),"confirmed");
  assert.ok((await (await account(await sessionCookie(human))).text()).includes(agent.accountId));
});

test("account_ConfirmBinding_KeysChangedSincePage_ShowsKeysChanged",{timeout:90_000},async()=>{
  const human=await seedHuman(),agent=await registerAgent("a"),view=await requestBinding(agent,human);
  const token=await tokenFor(human,"Confirm binding");
  // The agent's settled key adds a key after the owner's page was rendered.
  const slipped=agentKey(),keysUrl=mailboxOrigin+"/v1/keys";
  const added=await agentFetch(agent,"/v1/keys",{method:"POST",body:{proof:keyProof(slipped,{accountId:agent.accountId,audience:keysUrl}),
    endorsement:keyEndorsement(agent.key,{accountId:agent.accountId,audience:keysUrl,newKid:slipped.kid})}});
  assert.equal(added.status,200,await added.clone().text());
  const response=await postAccount(token,await sessionCookie(human));
  assert.equal(response.status,303);
  assert.equal(result(response),"keys-changed");
  assert.equal(await ownerOf(agent),null);
  assert.equal(await bindingStatus(agent,view.requestId),"pending");
  const page=await (await shared.mf.dispatchFetch(issuer+response.headers.get("location")!,{redirect:"manual",headers:{Cookie:await sessionCookie(human)}})).text();
  assert.ok(page.includes("The agent&#39;s keys changed after you opened this page."),page);
  assert.ok(page.includes(slipped.kid.slice(8)),"the reloaded page shows the new key");
  // Positive control: a token from the reloaded page confirms.
  assert.equal(result(await postAccount(await tokenFor(human,"Confirm binding"),await sessionCookie(human))),"done");
  assert.equal(await ownerOf(agent),human.humanId);
});

test("account_ConfirmBinding_Stale_RedirectsToGithubSelectAccountNoEffect",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await registerAgent("a"),view=await requestBinding(agent,human);
  const stale=Date.now()-600_001;
  const response=await postAccount(await tokenFor(human,"Confirm binding",0,stale),await sessionCookie(human,stale));
  assert.equal(response.status,302);
  const location=new URL(response.headers.get("location")!);
  assert.equal(location.host,"github.com");
  assert.equal(location.searchParams.get("prompt"),"select_account");
  assert.equal(location.searchParams.get("login"),human.login);
  assert.equal(await ownerOf(agent),null);
  assert.equal(await bindingStatus(agent,view.requestId),"pending");
});

async function humanViaGithub(id:number):Promise<Human>{
  const started=await beginGithubLogin({OAUTH_ISSUER:issuer,GITHUB_CLIENT_ID:"Iv1.test-client-id",HUMAN_SESSION_KEY:sessionKey},new URL(issuer+"/account"),Date.now());
  const state=new URL(started.headers.get("location")!).searchParams.get("state")!;
  const loginCookie=started.headers.getSetCookie().find(x=>x.startsWith("__Host-weave_login="))!.split(";")[0];
  const back=await shared.mf.dispatchFetch(issuer+"/login/github/callback?"+new URLSearchParams({code:"gh-"+id,state}),{redirect:"manual",headers:{Cookie:loginCookie}});
  assert.equal(back.status,302);
  const row=await (await db()).prepare("SELECT human_id FROM humans WHERE subject=?").bind(String(id)).first<{human_id:string}>();
  return {humanId:row!.human_id,subject:String(id),login:"human"+id};
}

test("account_ReauthCallback_ThenConfirm_Succeeds",{timeout:60_000},async()=>{
  const human=await humanViaGithub(7777);
  const agent=await registerAgent("a"),view=await requestBinding(agent,human);
  const stale=Date.now()-600_001,staleCookie=await sessionCookie(human,stale);
  const token=await tokenFor(human,"Confirm binding",0,stale);
  const redirect=await postAccount(token,staleCookie);
  assert.equal(redirect.status,302);
  const state=new URL(redirect.headers.get("location")!).searchParams.get("state")!;
  const loginCookie=redirect.headers.getSetCookie().find(x=>x.startsWith("__Host-weave_login="))!.split(";")[0];
  const back=await shared.mf.dispatchFetch(issuer+"/login/github/callback?"+new URLSearchParams({code:"gh-7777",state}),{redirect:"manual",headers:{Cookie:loginCookie}});
  assert.equal(back.status,302);
  assert.equal(new URL(back.headers.get("location")!).pathname,"/account");
  const fresh=back.headers.getSetCookie().find(x=>x.startsWith("__Host-weave_human="))!.split(";")[0];
  assert.equal(await ownerOf(agent),null,"signing in again must not decide anything");
  // The token minted for the stale view is still bound to this human and unexpired.
  const confirmed=await postAccount(token,fresh);
  assert.equal(confirmed.headers.get("location"),"/account?result=done");
  assert.equal(await ownerOf(agent),human.humanId);
  assert.equal(await bindingStatus(agent,view.requestId),"confirmed");
});

test("account_ReplayedConfirm_SingleEffectSecondNotPending",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await registerAgent("a");
  await requestBinding(agent,human);
  const token=await tokenFor(human,"Confirm binding"),cookie=await sessionCookie(human);
  assert.equal(result(await postAccount(token,cookie)),"done");
  assert.equal(result(await postAccount(token,cookie)),"not-pending");
  const n=await (await db()).prepare("SELECT count(*) n FROM binding_requests WHERE account_id=? AND status='confirmed'").bind(agent.accountId).first<number>("n");
  assert.equal(n,1);
});

test("account_CrossOriginPost_403NoEffect",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await registerAgent("a"),view=await requestBinding(agent,human);
  const token=await tokenFor(human,"Confirm binding");
  const response=await postAccount(token,await sessionCookie(human),"https://evil.example");
  assert.equal(response.status,403);
  assert.equal(await ownerOf(agent),null);
  assert.equal(await bindingStatus(agent,view.requestId),"pending");
});

test("account_MissingOrigin_403NoEffect",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await registerAgent("a"),view=await requestBinding(agent,human);
  const token=await tokenFor(human,"Confirm binding");
  const response=await postAccount(token,await sessionCookie(human),null);
  assert.equal(response.status,403);
  assert.equal(await ownerOf(agent),null);
  assert.equal(await bindingStatus(agent,view.requestId),"pending");
});

test("account_OtherHumansToken_NotFoundNoEffect",{timeout:60_000},async()=>{
  const alice=await seedHuman(),bob=await seedHuman(),agent=await registerAgent("a"),view=await requestBinding(agent,alice);
  const aliceToken=await tokenFor(alice,"Confirm binding");
  const response=await postAccount(aliceToken,await sessionCookie(bob));
  assert.equal(response.status,303);
  assert.equal(result(response),"not-found");
  assert.equal(await ownerOf(agent),null);
  assert.equal(await bindingStatus(agent,view.requestId),"pending");
});

test("account_RejectBinding_StaleSessionAllowed",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await registerAgent("a"),view=await requestBinding(agent,human);
  const stale=Date.now()-1_800_000;
  const response=await postAccount(await tokenFor(human,"Reject",0,stale),await sessionCookie(human,stale));
  assert.equal(result(response),"done");
  assert.equal(await bindingStatus(agent,view.requestId),"rejected");
  assert.equal(await ownerOf(agent),null);
});

test("account_ConfirmKeyRequestAndRevokeKey_Fresh_Done",{timeout:90_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human);
  const second=agentKey(),keysUrl=mailboxOrigin+"/v1/keys";
  const added=await agentFetch(agent,"/v1/keys",{method:"POST",body:{proof:keyProof(second,{accountId:agent.accountId,audience:keysUrl}),
    endorsement:keyEndorsement(agent.key,{accountId:agent.accountId,audience:keysUrl,newKid:second.kid})}});
  assert.equal(added.status,202,await added.clone().text());
  const html=await (await account(await sessionCookie(human))).text();
  assert.ok(html.includes("endorsed by")&&html.includes(second.kid.slice(8)));
  const confirmed=await postAccount(forms(html).find(x=>x.label==="Confirm")!.token,await sessionCookie(human));
  assert.equal(result(confirmed),"done");
  const live=async()=>(await (await db()).prepare("SELECT kid FROM agent_keys WHERE account_id=? AND revoked_at IS NULL ORDER BY kid").bind(agent.accountId)
    .all<{kid:string}>()).results.map(r=>r.kid);
  assert.deepEqual(await live(),[agent.key.kid,second.kid].sort());
  const revoke=forms(await (await account(await sessionCookie(human))).text()).filter(x=>x.label==="Revoke");
  assert.equal(revoke.length,2);
  assert.equal(result(await postAccount(revoke[0].token,await sessionCookie(human))),"done");
  assert.equal((await live()).length,1);
});

test("account_IssueRecoveryCode_Fresh_ShowsCodeOnceNotInLocation",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human);
  const token=await tokenFor(human,"Issue recovery code");
  logs.length=0;
  const response=await postAccount(token,await sessionCookie(human));
  assert.equal(response.status,200);
  assert.equal(response.headers.get("location"),null);
  assert.equal(response.headers.get("cache-control"),"no-store");
  assert.equal(response.headers.get("x-frame-options"),"DENY");
  const html=await response.text();
  const code=/<code>([A-Za-z0-9_-]{43})<\/code>/.exec(html)?.[1];
  assert.ok(code,"the page must show the 43-character code");
  assert.ok(html.includes(agent.accountId)&&html.includes("/v1/key-requests")&&html.includes("DPoP"));
  assert.ok(!(await (await account(await sessionCookie(human))).text()).includes(code),"the code is not on the owner page");
  assert.ok(logs.every(line=>!line.includes(code)),"the code must never be logged");
  // The code the page showed really redeems.
  const key=agentKey(),url=mailboxOrigin+"/v1/key-requests",worker=await mailbox();
  const redeemed=await worker.fetch(url,{method:"POST",headers:{"Content-Type":"application/json",DPoP:dpopProof(key,{method:"POST",url})},
    body:JSON.stringify({agentId:agent.accountId,code})});
  assert.equal(redeemed.status,200,await redeemed.clone().text());
});

test("account_IssueRecoveryCode_Stale_RedirectsToGithub",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human);
  const stale=Date.now()-600_001;
  const response=await postAccount(await tokenFor(human,"Issue recovery code",0,stale),await sessionCookie(human,stale));
  assert.equal(response.status,302);
  const location=new URL(response.headers.get("location")!);
  assert.equal(location.host,"github.com");
  assert.equal(location.searchParams.get("prompt"),"select_account");
  const issued=await (await db()).prepare("SELECT count(*) n FROM recovery_codes WHERE account_id=?").bind(agent.accountId).first<number>("n");
  assert.equal(issued,0);
});

test("account_HumanAtTenAgents_ShowsAgentLimit",{timeout:120_000},async()=>{
  const human=await seedHuman();
  for(let i=0;i<10;i++)await ownedAgent(human,"owned"+i);
  const agent=await registerAgent("eleventh"),view=await requestBinding(agent,human);
  const response=await postAccount(await tokenFor(human,"Confirm binding"),await sessionCookie(human));
  assert.equal(result(response),"agent-limit");
  const page=await (await shared.mf.dispatchFetch(issuer+response.headers.get("location")!,{redirect:"manual",headers:{Cookie:await sessionCookie(human)}})).text();
  assert.ok(page.includes("maximum of 10 agents"));
  assert.equal(await ownerOf(agent),null);
  assert.equal(await bindingStatus(agent,view.requestId),"pending");
});

test("account_MailboxRpcThrows_503AndLogsCategory",{timeout:60_000},async()=>{
  // The default export has no ownerOverview method, so the RPC call itself fails.
  const instance=await start({MAILBOX:{type:"worker",worker:mailboxName}});
  logs.length=0;
  try{
    const human=await seedHuman(instance.mf);
    const response=await account(await sessionCookie(human),instance.mf);
    assert.equal(response.status,503);
    assert.ok(logs.some(x=>x.includes("human-directory: call failed")),JSON.stringify(logs));
  }finally{await stop(instance);}
});

test("account_ExpiredFormToken_FormExpiredNoEffect",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await registerAgent("a"),view=await requestBinding(agent,human);
  // A reject token minted when a page was rendered more than 10 minutes ago.
  const token=await ownerActionToken(sessionKey,human.humanId,{kind:"reject-binding",requestId:view.requestId},Date.now()-600_001);
  const response=await postAccount(token,await sessionCookie(human));
  assert.equal(response.status,303);
  assert.equal(result(response),"form-expired");
  assert.equal(await bindingStatus(agent,view.requestId),"pending");
  const page=await (await shared.mf.dispatchFetch(issuer+response.headers.get("location")!,{redirect:"manual",headers:{Cookie:await sessionCookie(human)}})).text();
  assert.ok(page.includes("This page expired. Reload it and try again."));
});

test("account_PostWhenOwnerActionThrows_503AndLogsCategory",{timeout:60_000},async()=>{
  // The default export has no ownerAction method, so the RPC call itself fails.
  const instance=await start({MAILBOX:{type:"worker",worker:mailboxName}});
  try{
    const human=await seedHuman(instance.mf);
    const token=await ownerActionToken(sessionKey,human.humanId,{kind:"reject-binding",requestId:randomUUID()},Date.now());
    logs.length=0;
    const response=await postAccount(token,await sessionCookie(human),issuer,instance.mf);
    assert.equal(response.status,503);
    assert.equal(response.headers.get("location"),null);
    assert.ok(logs.some(x=>x.includes("human-directory: call failed")),JSON.stringify(logs));
    assert.ok(logs.every(x=>!x.includes(token)),"the form token is never logged");
  }finally{await stop(instance);}
});

test("consent_StillIssuesCode",{timeout:60_000},async()=>{
  const human=await seedHuman();
  const verifier=randomBytes(32).toString("base64url"),redirectUri="http://127.0.0.1:53621/callback",state="s-"+randomUUID();
  const url=new URL(issuer+"/authorize");
  for(const [k,v] of Object.entries({response_type:"code",client_id:demoOAuthClientId,redirect_uri:redirectUri,
    scope:"weave:messages.send",state,resource,code_challenge:createHash("sha256").update(verifier).digest("base64url"),code_challenge_method:"S256"}))url.searchParams.set(k,v);
  const session=await sessionCookie(human);
  const page=await shared.mf.dispatchFetch(url.href,{redirect:"manual",headers:{Cookie:session}});
  assert.equal(page.status,200);
  const handle=(await page.text()).match(/name="handle" value="([A-Za-z0-9_-]{43})"/)![1];
  const cookie=[session,...page.headers.getSetCookie().map(x=>x.split(";")[0])].join("; ");
  const approved=await shared.mf.dispatchFetch(issuer+"/authorize",{method:"POST",redirect:"manual",headers:{Origin:issuer,Cookie:cookie,
    "Content-Type":"application/x-www-form-urlencoded"},body:new URLSearchParams({handle,decision:"approve",agent:"new",agentName:"Smoke"}).toString()});
  assert.equal(approved.status,302);
  const redirect=new URL(approved.headers.get("location")!);
  assert.equal(redirect.origin+redirect.pathname,redirectUri);
  assert.ok(redirect.searchParams.get("code"));
});

test("account_ApproveExternalAction_Fresh_AgentSendsOnce",{timeout:90_000},async()=>{
  const human=await seedHuman(),content="Episode "+randomUUID();
  const {agent,action}=await proposeAction(human,content);
  const html=await (await account(await sessionCookie(human))).text();
  assert.ok(html.includes("<pre>"+content+"</pre>")&&html.includes("Desk Alpha (<code>urn:li:person:alpha</code>)"));
  assert.equal((await executeAction(agent,action.actionId)).status,"pending");

  const response=await postAccount(await tokenFor(human,"Approve and allow sending once"),await sessionCookie(human));
  assert.equal(response.status,303);
  assert.equal(response.headers.get("location"),"/account?result=done");
  assert.equal(providerCalls.filter(call=>call.actionId===action.actionId).length,0,"approval alone sends nothing");

  const sent=await executeAction(agent,action.actionId),again=await executeAction(agent,action.actionId);
  assert.equal(sent.status,"completed");
  assert.equal(sent.receipt,"post-"+action.actionId);
  assert.equal(again.status,"completed");
  assert.deepEqual(providerCalls.filter(call=>call.actionId===action.actionId),[{operation:"publish_text",content,targetAccount:"urn:li:person:alpha",actionId:action.actionId}]);
});

test("account_ApproveExternalAction_Stale_RedirectsToGithubNoEffect",{timeout:60_000},async()=>{
  const human=await seedHuman(),{agent,action}=await proposeAction(human,"stale "+randomUUID());
  const stale=Date.now()-11*60_000;
  const response=await postAccount(await tokenFor(human,"Approve and allow sending once",0,stale),await sessionCookie(human,stale));
  assert.equal(response.status,302);
  assert.equal(new URL(response.headers.get("location")!).origin,"https://github.com");
  assert.equal((await executeAction(agent,action.actionId)).status,"pending");
  assert.equal(providerCalls.filter(call=>call.actionId===action.actionId).length,0);
});

test("account_ApproveExternalAction_CredentialRotatedSincePage_PlanChanged",{timeout:60_000},async()=>{
  const human=await seedHuman(),{agent,action}=await proposeAction(human,"rotated "+randomUUID());
  const token=await tokenFor(human,"Approve and allow sending once");
  await (await db()).prepare("UPDATE external_credentials SET revision='rev-2' WHERE account_id=?").bind(agent.accountId).run();
  const response=await postAccount(token,await sessionCookie(human));
  assert.equal(result(response),"plan-changed");
  assert.equal((await executeAction(agent,action.actionId)).status,"pending");
  assert.equal(providerCalls.filter(call=>call.actionId===action.actionId).length,0);
});

// ---- the page for one action, which an app such as Envoylet links its owner to -------------------------------------------
const actionPage=(actionId:string,cookie?:string,query="")=>shared.mf.dispatchFetch(issuer+"/account/actions/"+actionId+query,
  {redirect:"manual",headers:cookie?{Cookie:cookie}:{}});
function postActionPage(actionId:string,token:string,cookie:string,origin:string|null=issuer){
  return shared.mf.dispatchFetch(issuer+"/account/actions/"+actionId,{method:"POST",redirect:"manual",headers:{
    "Content-Type":"application/x-www-form-urlencoded",...origin?{Origin:origin}:{},Cookie:cookie},body:new URLSearchParams({action:token}).toString()});
}
const pageToken=async(human:Human,actionId:string,label:string)=>{
  const found=forms(await (await actionPage(actionId,await sessionCookie(human))).text()).find(x=>x.label===label);
  assert.ok(found,"the action page must render a "+label+" form");
  return found.token;
};

test("actionPage_NoSessionOrStale_SignsInReturningToThisPageOnly",{timeout:60_000},async()=>{
  const human=await seedHuman(),{action}=await proposeAction(human,"sign in "+randomUUID());
  for(const cookie of [undefined,await sessionCookie(human,Date.now()-11*60_000)]){
    const response=await actionPage(action.actionId,cookie);
    assert.equal(response.status,302);
    const location=new URL(response.headers.get("location")!);
    assert.equal(location.origin+location.pathname,"https://github.com/login/oauth/authorize");
    if(cookie)assert.equal(location.searchParams.get("prompt"),"select_account","a stale sign-in signs in again before the page is shown");
    const login=response.headers.getSetCookie().find(x=>x.startsWith("__Host-weave_login="))!.split(";")[0].split("=")[1];
    assert.equal((JSON.parse(Buffer.from(login.split(".")[0],"base64url").toString()) as {returnTo:string}).returnTo,issuer+"/account/actions/"+action.actionId);
  }
  // A query never travels through sign-in: the return address is always the bare page.
  const queried=(await actionPage(action.actionId,undefined,"?next=https://evil.example")).headers.getSetCookie().find(x=>x.startsWith("__Host-weave_login="))!.split(";")[0].split("=")[1];
  assert.equal((JSON.parse(Buffer.from(queried.split(".")[0],"base64url").toString()) as {returnTo:string}).returnTo,issuer+"/account/actions/"+action.actionId);
  assert.equal((await shared.mf.dispatchFetch(issuer+"/account/actions/not-an-id",{redirect:"manual"})).status,404);
  assert.equal((await shared.mf.dispatchFetch(issuer+"/account/actions/"+action.actionId,{method:"DELETE",redirect:"manual"})).status,405);
});

test("actionPage_Fresh_ShowsOnlyThisActionAndApprovingSendsOnce",{timeout:90_000},async()=>{
  const human=await seedHuman(),content="Single "+randomUUID(),other="Other "+randomUUID();
  const {agent,action}=await proposeAction(human,content);
  const second=await proposeAction(human,other);
  // The mailbox hands the app this page's address; the app need not build it.
  assert.equal(action.approvalUrl,issuer+"/account/actions/"+action.actionId);
  const linked=await shared.mf.dispatchFetch(action.approvalUrl!,{redirect:"manual",headers:{Cookie:await sessionCookie(human)}});
  assert.equal(linked.status,200);
  const html=await linked.text();
  assert.ok(html.includes("<pre>"+content+"</pre>")&&!html.includes(other),"only the linked action is shown");
  assert.ok(html.includes('action="/account/actions/'+action.actionId+'"'),"its forms post back to this page");
  const response=await postActionPage(action.actionId,await pageToken(human,action.actionId,"Approve and allow sending once"),await sessionCookie(human));
  assert.equal(response.status,200);
  assert.equal(response.headers.get("location"),null,"the decision is answered with its outcome, not redirected");
  assert.ok((await response.text()).includes("Approved. The agent may now send it once."));
  assert.equal((await readAction(agent,action.actionId)).approvalUrl,null,"a decided action has no page to decide on");
  assert.equal((await readAction(second.agent,second.action.actionId)).approvalUrl,issuer+"/account/actions/"+second.action.actionId);
  assert.equal(providerCalls.filter(call=>call.actionId===action.actionId).length,0,"approval alone sends nothing");
  assert.equal((await executeAction(agent,action.actionId)).status,"completed");
  assert.equal((await executeAction(agent,action.actionId)).status,"completed");
  assert.equal(providerCalls.filter(call=>call.actionId===action.actionId).length,1,"sent once");
  assert.ok((await (await actionPage(action.actionId,await sessionCookie(human))).text()).includes("not waiting for your approval"));
  assert.equal((await executeAction(second.agent,second.action.actionId)).status,"pending","the other action is untouched");
});

test("actionPage_Reject_NothingSent",{timeout:60_000},async()=>{
  const human=await seedHuman(),{agent,action}=await proposeAction(human,"reject "+randomUUID());
  const response=await postActionPage(action.actionId,await pageToken(human,action.actionId,"Reject"),await sessionCookie(human));
  assert.ok((await response.text()).includes("Rejected. Nothing will be sent."));
  assert.equal((await executeAction(agent,action.actionId)).status,"rejected");
  assert.equal(providerCalls.filter(call=>call.actionId===action.actionId).length,0);
});

test("actionPage_ForgedResultQuery_ClaimsNoDecision",{timeout:60_000},async()=>{
  // A link cannot make the page claim an outcome: the query is ignored, whatever the action's real state.
  const human=await seedHuman(),{agent,action}=await proposeAction(human,"forged "+randomUUID());
  const approved=await proposeAction(human,"approved "+randomUUID());
  await postActionPage(approved.action.actionId,await pageToken(human,approved.action.actionId,"Approve and allow sending once"),await sessionCookie(human));
  for(const [actionId,query] of [[action.actionId,"?result=approved"],[action.actionId,"?result=rejected"],[approved.action.actionId,"?result=rejected"]]){
    const html=await (await actionPage(actionId,await sessionCookie(human),query)).text();
    assert.ok(!html.includes("Approved.")&&!html.includes("Rejected.")&&!html.includes('role="status"'),"no outcome claimed for "+query);
  }
  assert.ok(forms(await (await actionPage(action.actionId,await sessionCookie(human),"?result=approved")).text()).length===2,"a pending action still offers its decision");
  assert.equal((await executeAction(agent,action.actionId)).status,"pending");
});

test("actionPage_AnotherActionsTokenOrAnotherHuman_NotFoundNoEffect",{timeout:90_000},async()=>{
  const human=await seedHuman(),{agent,action}=await proposeAction(human,"mine "+randomUUID());
  const second=await proposeAction(human,"second "+randomUUID());
  // A signed approval of the second action, posted to the first action's page, decides nothing.
  const misplaced=await postActionPage(action.actionId,await pageToken(human,second.action.actionId,"Approve and allow sending once"),await sessionCookie(human));
  assert.ok((await misplaced.text()).includes("That request, key or connected client was not found."));
  assert.equal((await executeAction(second.agent,second.action.actionId)).status,"pending");
  // An owner-page token for an unrelated decision is not taken here either.
  const ownerToken=await tokenFor(human,"Issue recovery code");
  assert.ok((await (await postActionPage(action.actionId,ownerToken,await sessionCookie(human))).text()).includes("was not found"));
  // Another human sees the same "not waiting" page, with no forms, and cannot decide.
  const stranger=await seedHuman();
  const html=await (await actionPage(action.actionId,await sessionCookie(stranger))).text();
  assert.ok(html.includes("not waiting for your approval")&&forms(html).length===0);
  const forged=await postActionPage(action.actionId,await pageToken(human,action.actionId,"Approve and allow sending once"),await sessionCookie(stranger));
  assert.ok((await forged.text()).includes("was not found"));
  assert.equal((await postActionPage(action.actionId,await pageToken(human,action.actionId,"Reject"),await sessionCookie(human),"https://evil.example")).status,403);
  assert.equal((await executeAction(agent,action.actionId)).status,"pending");
});

/** Starts connecting YouTube from the owner page and returns Google's redirect, the state and the pending-connection cookie. */
async function beginConnect(human:Human,agent:Agent){
  const html=await (await account(await sessionCookie(human))).text();
  const found=forms(html).filter(x=>x.label==="Connect YouTube");
  assert.ok(found.length>=1,"the page offers Connect YouTube");
  const response=await postAccount(found[found.length-1].token,await sessionCookie(human));
  assert.equal(response.status,302);
  const location=new URL(response.headers.get("location")!);
  const cookie=response.headers.getSetCookie().find(x=>x.startsWith("__Host-weave_connect="))!.split(";")[0];
  return {location,state:location.searchParams.get("state")!,cookie,agent};
}
const callback=async(human:Human,state:string,code:string,cookie:string)=>shared.mf.dispatchFetch(issuer+"/connect/youtube/callback?"+
  new URLSearchParams({state,code}),{redirect:"manual",headers:{Cookie:(await sessionCookie(human))+"; "+cookie}});

test("connectYoutube_OwnerConsents_StoresVerifiedChannelAndPageShowsIt",{timeout:90_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human,"connector");
  const {location,state,cookie}=await beginConnect(human,agent);
  assert.equal(location.origin+location.pathname,"https://accounts.google.com/o/oauth2/v2/auth");
  assert.equal(location.searchParams.get("redirect_uri"),issuer+"/connect/youtube/callback");
  assert.equal(location.searchParams.get("access_type"),"offline");
  assert.equal(location.searchParams.get("code_challenge_method"),"S256");
  assert.equal(location.searchParams.get("scope"),youtubeScope);
  const done=await callback(human,state,"yt-4",cookie);
  assert.equal(done.status,303);
  assert.equal(done.headers.get("location"),"/account?result=connected");
  const stored=await (await db()).prepare("SELECT target_account,target_display,secret IS NOT NULL sealed FROM external_credentials WHERE account_id=?")
    .bind(agent.accountId).first<{target_account:string;target_display:string;sealed:number}>();
  assert.deepEqual(stored,{target_account:"UC"+"4".repeat(22),target_display:"Channel 4",sealed:1});
  assert.ok((await (await account(await sessionCookie(human))).text()).includes("youtube as Channel 4 (<code>UC"+"4".repeat(22)+"</code>)"));
});

test("connectYoutube_WrongStateOrAnotherHuman_FormExpiredNothingStored",{timeout:90_000},async()=>{
  const human=await seedHuman(),other=await seedHuman(),agent=await ownedAgent(human,"connector");
  const {state,cookie}=await beginConnect(human,agent);
  assert.equal(result(await callback(human,"x".repeat(43),"yt-5",cookie)),"form-expired");
  assert.equal(result(await callback(other,state,"yt-5",cookie)),"form-expired");
  assert.equal(result(await shared.mf.dispatchFetch(issuer+"/connect/youtube/callback?"+new URLSearchParams({state,error:"access_denied"}),
    {redirect:"manual",headers:{Cookie:(await sessionCookie(human))+"; "+cookie}})),"connect-cancelled");
  assert.equal(await (await db()).prepare("SELECT count(*) n FROM external_credentials WHERE account_id=?").bind(agent.accountId).first<number>("n"),0);
});

test("connectYoutube_StaleSession_ReauthenticatesBeforeLeavingForGoogle",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human,"connector"),stale=Date.now()-11*60_000;
  const token=await tokenFor(human,"Connect YouTube",0,stale);
  const response=await postAccount(token,await sessionCookie(human,stale));
  assert.equal(new URL(response.headers.get("location")!).origin,"https://github.com");
  assert.equal(await (await db()).prepare("SELECT count(*) n FROM external_credentials WHERE account_id=?").bind(agent.accountId).first<number>("n"),0);
});

/** The signed token of the Connect form for one platform, found by its button. */
function connectToken(html:string,label:string):string{
  const token=new RegExp('<form method="post" action="/account/connect"><input type="hidden" name="action" value="([^"]+)">(?:(?!</form>).)*?<button type="submit">Connect '+label+'</button>').exec(html)?.[1];
  assert.ok(token,"the page offers Connect "+label);
  return token;
}
const postizToken=(html:string)=>connectToken(html,"Postiz");
const postConnect=async(human:Human,token:string,fields:Record<string,string>,authAt?:number)=>shared.mf.dispatchFetch(issuer+"/account/connect",{method:"POST",redirect:"manual",
  headers:{"Content-Type":"application/x-www-form-urlencoded",Origin:issuer,Cookie:await sessionCookie(human,authAt)},body:new URLSearchParams({action:token,...fields}).toString()});
const postPostiz=(human:Human,token:string,apiKey:string,authAt?:number)=>postConnect(human,token,{apiKey},authAt);

test("connectPostiz_OwnerSubmitsKey_ChannelsStoredSealedAndListed",{timeout:90_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human,"publisher");
  const token=postizToken(await (await account(await sessionCookie(human))).text());
  const response=await postPostiz(human,token,postizKey);
  assert.equal(response.status,303);
  assert.equal(response.headers.get("location"),"/account?result=account-connected");
  const rows=(await (await db()).prepare("SELECT credential_ref,target_account,secret IS NOT NULL sealed FROM external_credentials WHERE account_id=?").bind(agent.accountId).all()).results;
  assert.deepEqual(rows,[{credential_ref:"postiz:cm4tiktok00000000000000001",target_account:"tiktok/cm4tiktok00000000000000001",sealed:1}]);
  const page=await (await account(await sessionCookie(human))).text();
  assert.ok(page.includes("postiz as tiktok · HSC Beats (@hscbeats)"));
  assert.ok(!page.includes(postizKey),"the key is never rendered");
  assert.equal(result(await postPostiz(human,token,"wrong-key-00000000000")),"provider-refused");
});

test("connectPostiz_StaleSessionOrSingleFieldPost_NothingStored",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human,"publisher"),stale=Date.now()-11*60_000;
  const token=postizToken(await (await account(await sessionCookie(human,stale))).text());
  const response=await postPostiz(human,token,postizKey,stale);
  assert.equal(new URL(response.headers.get("location")!).origin,"https://github.com");
  assert.equal(result(await postAccount(token,await sessionCookie(human))),"not-found","the field-less /account form cannot connect Postiz");
  assert.equal(await (await db()).prepare("SELECT count(*) n FROM external_credentials WHERE account_id=?").bind(agent.accountId).first<number>("n"),0);
});

const blueskyToken=(html:string)=>connectToken(html,"Bluesky");
const postBluesky=(human:Human,token:string,fields:Record<string,string>,authAt?:number)=>postConnect(human,token,fields,authAt);

test("connectBluesky_OwnerSubmitsAppPassword_AccountStoredSealedAndListed",{timeout:90_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human,"poster");
  const token=blueskyToken(await (await account(await sessionCookie(human))).text());
  const response=await postBluesky(human,token,{handle:"@"+blueskyHandle,appPassword:blueskyPassword});
  assert.equal(response.headers.get("location"),"/account?result=account-connected");
  const rows=(await (await db()).prepare("SELECT credential_ref,target_account,target_display,secret IS NOT NULL sealed FROM external_credentials WHERE account_id=?").bind(agent.accountId).all()).results;
  assert.deepEqual(rows,[{credential_ref:"bluesky:"+blueskyDid,target_account:blueskyDid,target_display:"@"+blueskyHandle,sealed:1}]);
  const page=await (await account(await sessionCookie(human))).text();
  assert.ok(page.includes("bluesky as @"+blueskyHandle));
  assert.ok(!page.includes(blueskyPassword),"the app password is never rendered");
  assert.equal(result(await postBluesky(human,token,{handle:blueskyHandle,appPassword:"zzzz-zzzz-zzzz-zzzz"})),"provider-refused");
});

test("connectLinkedIn_ThousandCharacterToken_PassesBothWorkersAndIsStoredSealed",{timeout:90_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human,"linker");
  const token=connectToken(await (await account(await sessionCookie(human))).text(),"LinkedIn");
  const response=await postConnect(human,token,{accessToken:linkedinToken});
  assert.equal(response.headers.get("location"),"/account?result=account-connected");
  const rows=(await (await db()).prepare("SELECT credential_ref,target_account,secret IS NOT NULL sealed FROM external_credentials WHERE account_id=?").bind(agent.accountId).all()).results;
  assert.deepEqual(rows,[{credential_ref:"linkedin:"+linkedinMember,target_account:"urn:li:person:"+linkedinMember,sealed:1}]);
  assert.ok(!(await (await account(await sessionCookie(human))).text()).includes(linkedinToken.slice(-40)),"the token is never rendered");
});

test("connectBluesky_StaleSessionMissingFieldOrKeylessForm_NothingStored",{timeout:60_000},async()=>{
  const human=await seedHuman(),agent=await ownedAgent(human,"poster"),stale=Date.now()-11*60_000;
  const token=blueskyToken(await (await account(await sessionCookie(human,stale))).text());
  const response=await postBluesky(human,token,{handle:blueskyHandle,appPassword:blueskyPassword},stale);
  assert.equal(new URL(response.headers.get("location")!).origin,"https://github.com");
  assert.equal(result(await postBluesky(human,token,{handle:blueskyHandle},Date.now())),"not-found","the app password is required");
  assert.equal(result(await postBluesky(human,blueskyToken(await (await account(await sessionCookie(human))).text()),{handle:blueskyHandle,appPassword:blueskyPassword,extra:"x"})),"not-found",
    "exactly the platform's fields");
  assert.equal(result(await postAccount(token,await sessionCookie(human))),"not-found","the password-less /account form cannot connect Bluesky");
  assert.equal(await (await db()).prepare("SELECT count(*) n FROM external_credentials WHERE account_id=?").bind(agent.accountId).first<number>("n"),0);
});

test("ownerMedia_OnlyTheOwnerSeesFilesTheActionNames",{timeout:90_000},async()=>{
  const human=await seedHuman(),other=await seedHuman(),agent=await ownedAgent(human,"publisher");
  await postPostiz(human,postizToken(await (await account(await sessionCookie(human))).text()),postizKey);
  const bucket=await shared.mf.getR2Bucket("EXTERNAL_MEDIA",mailboxName),bytes=randomBytes(4000),mediaId=randomUUID(),unnamed=randomUUID();
  const sha256=createHash("sha256").update(bytes).digest("hex"),at=(BigInt(Date.now())*10000n).toString();
  for(const id of [mediaId,unnamed]){
    await bucket.put(agent.accountId+"/"+id,bytes);
    await (await db()).prepare("INSERT INTO external_media(media_id,account_id,content_type,bytes,sha256,created_at) VALUES(?,?,'image/png',?,?,CAST(? AS INTEGER))")
      .bind(id,agent.accountId,bytes.length,sha256,at).run();
  }
  const proposed=await agentFetch(agent,"/v1/external-actions",{method:"POST",body:{credentialRef:"postiz:cm4tiktok00000000000000001",operation:"publish_post",
    content:JSON.stringify({text:"",media:[{mediaId,bytes:bytes.length,sha256}],settings:{__type:"tiktok",privacy_level:"SELF_ONLY",duet:false,stitch:false,comment:false,
      autoAddMusic:"no",brand_content_toggle:false,brand_organic_toggle:false,content_posting_method:"DIRECT_POST"}})}});
  assert.equal(proposed.status,200,await proposed.clone().text());
  const {actionId}=await proposed.json() as {actionId:string};
  assert.ok((await (await account(await sessionCookie(human))).text()).includes('src="/account/media/'+actionId+'/'+mediaId+'"'));
  const preview=(who:Human,id:string)=>shared.mf.dispatchFetch(issuer+"/account/media/"+actionId+"/"+id,{headers:{Cookie:""}}).then(async()=>
    shared.mf.dispatchFetch(issuer+"/account/media/"+actionId+"/"+id,{headers:{Cookie:await sessionCookie(who)}}));
  const own=await preview(human,mediaId);
  assert.equal(own.status,200);
  assert.equal(own.headers.get("content-type"),"image/png");
  assert.ok(Buffer.from(await own.arrayBuffer()).equals(bytes));
  assert.equal((await preview(other,mediaId)).status,404,"another human sees nothing");
  assert.equal((await preview(human,unnamed)).status,404,"media the action does not name is not served");
  assert.equal((await shared.mf.dispatchFetch(issuer+"/account/media/"+actionId+"/"+mediaId)).status,404,"no session, no file");
  const humans=await db();
  await humans.prepare("UPDATE humans SET frozen_at=updated_at WHERE human_id=?").bind(human.humanId).run();
  try{
    const frozen=await preview(human,mediaId);
    assert.equal(frozen.status,404,"a frozen owner's still-signed session gets no file");
  }finally{await humans.prepare("UPDATE humans SET frozen_at=NULL WHERE human_id=?").bind(human.humanId).run();}
  const thawed=await preview(human,mediaId);
  assert.equal(thawed.status,200,"the same owner sees the file again once unfrozen");
  assert.ok(Buffer.from(await thawed.arrayBuffer()).equals(bytes));
  await bucket.delete(agent.accountId+"/"+mediaId);
  assert.equal((await preview(human,mediaId)).status,404,"stored bytes gone: 404, not an error page");
  await bucket.put(agent.accountId+"/"+mediaId,bytes);
  await humans.prepare("DELETE FROM external_media WHERE media_id=?").bind(mediaId).run();
  assert.equal((await preview(human,mediaId)).status,404,"media record gone: the object alone is not served");
});
