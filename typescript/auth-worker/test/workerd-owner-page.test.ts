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
          MAILBOX:{type:"worker",worker:mailboxName,exportName:"HumanDirectory"},
          ...authEnv
        },
        exports:{ConsentNonce:{type:"durable-object",storage:"sqlite"}}
      }
    },{
      config:{
        name:mailboxName,compatibilityDate:"2026-10-13",compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"mailbox.js",modules:{"mailbox.js":{type:"esm",contents:mailboxBundle}}},
        env:{
          db:{type:"d1"},
          REGISTRATION_ENABLED:{type:"text",value:"true"},
          REGISTRATION_MAX_ACCOUNTS:{type:"text",value:"1000"},
          REGISTRATION_PER_IP_PER_HOUR:{type:"text",value:"1000"},
          REGISTRATION_GLOBAL_PER_HOUR:{type:"text",value:"1000"},
          CLEANUP_MAX_MESSAGES:{type:"text",value:"0"},
          CLEANUP_TIME_BUDGET_MS:{type:"text",value:"0"}
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
