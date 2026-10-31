import assert from "node:assert/strict";
import {createHash,randomBytes,randomUUID} from "node:crypto";
import {readFileSync} from "node:fs";
import {mkdtemp,rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {after,before,test} from "node:test";
import {Miniflare,type Response as MiniflareResponse} from "miniflare";
import {initializeDatabase} from "../../mailbox-worker/scripts/latest-schema.ts";
import {beginGithubLogin,boundedProviderJson} from "../src/github-login.ts";
import {signedCookieValue} from "../src/human-session.ts";

const issuer="https://auth.weave.example";
const resource="https://demo.weave-389.workers.dev/mcp";
const githubClientId="Iv1.test-client-id";
const githubClientSecret=randomBytes(20).toString("hex");
const fakeAccessToken="gho_"+randomBytes(18).toString("hex");
const fakeCreatedAt="2011-01-25T18:44:36Z";
const sessionKey=randomBytes(32).toString("base64url");
const bundled=readFileSync(resolve("dist/index.js"),"utf8");
const mailboxBundle=readFileSync(resolve("../mailbox-worker/dist/index.js"),"utf8");

// Only the two GitHub endpoints exist; every other outbound request is denied.
type Recorded={url:string;method:string;headers:Record<string,string>;body:string};
const github={requests:[] as Recorded[],userStatus:200,redirectToken:false,redirectUser:false,oversizedToken:false,oversizedUser:false,slowToken:false,slowUser:false};
async function fakeGithub(request:Request):Promise<Response>{
  const body=await request.text();
  github.requests.push({url:request.url,method:request.method,
    headers:Object.fromEntries(request.headers),body});
  if(request.method==="POST"&&request.url==="https://github.com/login/oauth/access_token"){
    const form=new URLSearchParams(body);
    if(form.get("client_secret")!==githubClientSecret||form.get("code")!=="fake-code")
      return Response.json({error:"bad_verification_code"});
    if(github.redirectToken)return new Response(null,{status:307,
      headers:{Location:"https://redirect-sink.example/credentials"}});
    if(github.slowToken)await new Promise<void>(resolve=>setTimeout(resolve,5_500));
    return Response.json({access_token:fakeAccessToken,token_type:"bearer",scope:"read:user",
      ...(github.oversizedToken?{unexpectedPadding:"x".repeat(64*1024)}:{})});
  }
  if(request.method==="GET"&&request.url==="https://api.github.com/user"){
    if(request.headers.get("authorization")!=="Bearer "+fakeAccessToken)return new Response("Bad credentials",{status:401});
    if(github.redirectUser)return new Response(null,{status:307,
      headers:{Location:"https://redirect-sink.example/identity"}});
    if(github.slowUser)await new Promise<void>(resolve=>setTimeout(resolve,5_500));
    if(github.userStatus!==200)return new Response("upstream failure",{status:github.userStatus});
    return Response.json({id:4242,login:"octo-human",created_at:fakeCreatedAt,email:"private@example.com",
      ...(github.oversizedUser?{unexpectedPadding:"x".repeat(64*1024)}:{})});
  }
  return new Response("Outbound denied",{status:403});
}

const logs:string[]=[];
function options(storage:string,authEnv:Record<string,unknown>={},mailboxEnv:Record<string,unknown>={}){
  return {
    resourcePersistencePath:storage,
    cf:false,
    handleStructuredLogs:(log:{message:string})=>{logs.push(log.message);},
    workers:[{
      dev:{outboundService:{type:"fetcher",handler:fakeGithub}},
      config:{
        name:"weave-auth-login",
        compatibilityDate:"2026-10-13",
        compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"index.js",modules:{"index.js":{type:"esm",contents:bundled}}},
        env:{
          OAUTH_KV:{type:"kv"},
          CONSENT_NONCES:{type:"durable-object",worker:"weave-auth-login",exportName:"ConsentNonce"},
          OAUTH_ISSUER:{type:"text",value:issuer},
          OAUTH_RESOURCE:{type:"text",value:resource},
          GITHUB_CLIENT_ID:{type:"text",value:githubClientId},
          GITHUB_CLIENT_SECRET:{type:"text",value:githubClientSecret},
          HUMAN_SESSION_KEY:{type:"text",value:sessionKey},
          MAILBOX:{type:"worker",worker:"weave-mailbox-login",exportName:"HumanDirectory"},
          ...authEnv
        },
        exports:{ConsentNonce:{type:"durable-object",storage:"sqlite"}}
      }
    },{
      config:{
        name:"weave-mailbox-login",
        compatibilityDate:"2026-10-13",
        compatibilityFlags:["nodejs_compat","global_fetch_strictly_public"],
        manifest:{mainModule:"mailbox.js",modules:{"mailbox.js":{type:"esm",contents:mailboxBundle}}},
        env:{
          db:{type:"d1"},
          REGISTRATION_ENABLED:{type:"text",value:"true"},
          REGISTRATION_MAX_ACCOUNTS:{type:"text",value:"10"},
          REGISTRATION_PER_IP_PER_HOUR:{type:"text",value:"10"},
          REGISTRATION_GLOBAL_PER_HOUR:{type:"text",value:"10"},
          CLEANUP_MAX_MESSAGES:{type:"text",value:"0"},
          CLEANUP_TIME_BUDGET_MS:{type:"text",value:"0"},
          ...mailboxEnv
        }
      }
    }]
  };
}

const exposed:string[]=[];
async function capture(response:MiniflareResponse){
  const body=await response.text();
  exposed.push(body,JSON.stringify([...response.headers]));
  return {status:response.status,headers:response.headers,body};
}
const loginEnv={OAUTH_ISSUER:issuer,GITHUB_CLIENT_ID:githubClientId,HUMAN_SESSION_KEY:sessionKey};
const authorizeUrl=()=>new URL(issuer+"/authorize?response_type=code&client_id=https%3A%2F%2Fclient.example%2Fcimd&state="+randomUUID());
async function begin(returnTo=authorizeUrl()){
  const response=await beginGithubLogin(loginEnv,returnTo,Date.now());
  const location=response.headers.get("location");
  const loginCookie=response.headers.getSetCookie().find(x=>x.startsWith("__Host-weave_login="));
  return {response,location:location?new URL(location):null,loginCookie,returnTo};
}
const cookieHeader=(setCookie:string)=>setCookie.split(";")[0];
const sessionCookie=(headers:{getSetCookie():string[]})=>headers.getSetCookie().find(x=>x.startsWith("__Host-weave_human="));

let shared:{mf:Miniflare;storage:string};
async function start(authEnv?:Record<string,unknown>,mailboxEnv?:Record<string,unknown>){
  const storage=await mkdtemp(join(tmpdir(),"weave-auth-login-"));
  const mf=new Miniflare(options(storage,authEnv,mailboxEnv));
  await initializeDatabase(await mf.getD1Database("db","weave-mailbox-login"),BigInt(Date.now())*10000n);
  return {mf,storage};
}
async function stop(instance:{mf:Miniflare;storage:string}){
  await instance.mf.dispose();
  await rm(instance.storage,{recursive:true,force:true});
}
async function humans(mf:Miniflare){
  const db=await mf.getD1Database("db","weave-mailbox-login");
  return (await db.prepare("SELECT human_id,subject,provider_created_at,display_login FROM humans").all()).results;
}
async function callback(mf:Miniflare,query:string,cookie?:string){
  return capture(await mf.dispatchFetch(issuer+"/login/github/callback?"+query,
    {redirect:"manual",headers:cookie?{Cookie:cookie}:{}}));
}

before(async()=>{shared=await start();},{timeout:60_000});
after(async()=>{
  await stop(shared);
  for(const text of [...exposed,...logs])
    assert.ok(!text.includes(fakeAccessToken),"the GitHub access token must never reach a response or log");
});

test("callback_ValidCodeAndState_SetsSessionAndRedirectsToReturnTo",{timeout:60_000},async()=>{
  github.requests.length=0;
  const started=await begin();
  assert.equal(started.response.status,302);
  const location=started.location!;
  assert.equal(location.origin+location.pathname,"https://github.com/login/oauth/authorize");
  assert.equal(location.searchParams.get("client_id"),githubClientId);
  assert.equal(location.searchParams.get("redirect_uri"),issuer+"/login/github/callback");
  assert.equal(location.searchParams.get("scope"),"read:user");
  assert.equal(location.searchParams.get("code_challenge_method"),"S256");
  const state=location.searchParams.get("state")!,challenge=location.searchParams.get("code_challenge")!;
  assert.match(state,/^[A-Za-z0-9_-]{43}$/);
  assert.ok(started.loginCookie);
  for(const attribute of ["Secure","HttpOnly","SameSite=Lax","Path=/","Max-Age=600"])
    assert.ok(started.loginCookie.split(";").map(x=>x.trim()).includes(attribute),attribute);

  const result=await callback(shared.mf,new URLSearchParams({code:"fake-code",state}).toString(),cookieHeader(started.loginCookie));
  assert.equal(result.status,302);
  assert.equal(result.headers.get("location"),started.returnTo.href);
  const exchange=github.requests.find(x=>x.url==="https://github.com/login/oauth/access_token")!;
  assert.ok(exchange,"the code must be exchanged at GitHub");
  assert.equal(exchange.headers.accept,"application/json");
  const form=new URLSearchParams(exchange.body);
  assert.equal(form.get("client_id"),githubClientId);
  assert.equal(form.get("redirect_uri"),issuer+"/login/github/callback");
  assert.equal(createHash("sha256").update(form.get("code_verifier")!).digest("base64url"),challenge,
    "the token exchange must carry the verifier for the begin redirect's challenge");
  const user=github.requests.find(x=>x.url==="https://api.github.com/user")!;
  assert.equal(user.headers["user-agent"],"weave-auth");
  const rows=await humans(shared.mf);
  assert.equal(rows.length,1);
  assert.equal(rows[0].subject,"4242");
  assert.equal(rows[0].provider_created_at,fakeCreatedAt);
  assert.equal(rows[0].display_login,"octo-human");
  assert.ok(sessionCookie(result.headers),"the human session must be set");
  const cleared=result.headers.getSetCookie().find(x=>x.startsWith("__Host-weave_login="));
  assert.ok(cleared&&/Max-Age=0/.test(cleared),"the login cookie must be cleared");
});

test("callback_StateMismatch_Returns400WithoutSession",{timeout:30_000},async()=>{
  github.requests.length=0;
  const started=await begin();
  const result=await callback(shared.mf,"code=fake-code&state="+randomBytes(32).toString("base64url"),cookieHeader(started.loginCookie!));
  assert.equal(result.status,400);
  assert.equal(sessionCookie(result.headers),undefined);
  assert.equal(github.requests.length,0,"no code exchange without a matching state");
  const missingCookie=await callback(shared.mf,"code=fake-code&state="+started.location!.searchParams.get("state"));
  assert.equal(missingCookie.status,400);
  assert.equal(sessionCookie(missingCookie.headers),undefined);
  assert.equal(github.requests.length,0);
});

test("callback_AccessDenied_ShowsCancelledWithoutSession",{timeout:30_000},async()=>{
  github.requests.length=0;
  const started=await begin();
  const state=started.location!.searchParams.get("state")!;
  const result=await callback(shared.mf,new URLSearchParams({error:"access_denied",
    error_description:"The user has denied your application access.",state}).toString(),cookieHeader(started.loginCookie!));
  assert.equal(result.status,400);
  assert.match(result.body,/cancelled/i);
  assert.equal(sessionCookie(result.headers),undefined);
  assert.equal(github.requests.length,0);
});

test("begin_OffOriginReturnTo_Rejected",async()=>{
  for(const target of ["https://evil.example/authorize","http://auth.weave.example/authorize",
    "https://user@auth.weave.example/authorize"]){
    const started=await begin(new URL(target));
    assert.equal(started.response.status,400,target);
    assert.equal(started.location,null);
    assert.equal(started.loginCookie,undefined);
  }
});

test("begin_NonAuthorizePath_Rejected",async()=>{
  for(const target of [issuer+"/oauth/token",issuer+"/authorize/",issuer+"/",issuer+"/authorize#x"]){
    const started=await begin(new URL(target));
    assert.equal(started.response.status,400,target);
    assert.equal(started.loginCookie,undefined);
  }
});

test("callback_ForgedOffOriginReturnTo_RejectedAtCompletion",{timeout:30_000},async()=>{
  github.requests.length=0;
  const state=randomBytes(32).toString("base64url");
  const value=await signedCookieValue(sessionKey,"github-login",{state,verifier:randomBytes(32).toString("base64url"),
    returnTo:"https://evil.example/authorize",expiresAt:Date.now()+600_000});
  const result=await callback(shared.mf,"code=fake-code&state="+state,"__Host-weave_login="+value);
  assert.equal(result.status,400);
  assert.equal(result.headers.get("location"),null);
  assert.equal(github.requests.length,0);
});

test("callback_ExpiredLoginCookie_Returns400WithoutGithubCalls",{timeout:30_000},async()=>{
  github.requests.length=0;
  const state=randomBytes(32).toString("base64url");
  const value=await signedCookieValue(sessionKey,"github-login",{state,verifier:randomBytes(32).toString("base64url"),
    returnTo:authorizeUrl().href,expiresAt:Date.now()-1});
  const result=await callback(shared.mf,"code=fake-code&state="+state,"__Host-weave_login="+value);
  assert.equal(result.status,400);
  assert.equal(sessionCookie(result.headers),undefined);
  assert.equal(github.requests.length,0,"an expired login cookie must not reach GitHub");
});

test("callback_GithubProviderRedirects_FailClosedWithoutForwardingSecrets",{timeout:60_000},async()=>{
  const before=(await humans(shared.mf)).length;
  for(const phase of ["token","user"] as const){
    github.requests.length=0;
    logs.length=0;
    github.redirectToken=phase==="token";
    github.redirectUser=phase==="user";
    try{
      const started=await begin();
      const state=started.location!.searchParams.get("state")!;
      const result=await callback(shared.mf,new URLSearchParams({code:"fake-code",state}).toString(),
        cookieHeader(started.loginCookie!));
      assert.equal(result.status,502,phase+" redirect must not create a session");
      assert.equal(sessionCookie(result.headers),undefined);
      assert.equal((await humans(shared.mf)).length,before);
      assert.ok(!github.requests.some(x=>x.url.startsWith("https://redirect-sink.example/")),
        phase+" redirect must not receive a GitHub credential");
      const expectedUrls=phase==="token"
        ?["https://github.com/login/oauth/access_token"]
        :["https://github.com/login/oauth/access_token","https://api.github.com/user"];
      assert.deepEqual(github.requests.map(x=>x.url),expectedUrls,
        phase+" must contact only the two fixed GitHub endpoints");
      // Miniflare's outbound-service adapter does not preserve the caller's redirect mode
      // on the handler Request; verify actual requests, credential destinations and outcome.
      const exchange=github.requests[0];
      assert.equal(new URLSearchParams(exchange.body).get("client_secret"),githubClientSecret);
      if(phase==="user"){
        const who=github.requests[1];
        assert.equal(who.headers.authorization,"Bearer "+fakeAccessToken);
      }
      assert.ok(logs.some(x=>x.includes(phase==="token"
        ?"github-login: token exchange failed":"github-login: user fetch failed")));
      assert.ok(!logs.some(x=>x.includes(githubClientSecret)||x.includes(fakeAccessToken)));
    }finally{github.redirectToken=false;github.redirectUser=false;}
  }
});

test("callback_OversizedGithubResponses_FailClosedBeforeSession",{timeout:60_000},async()=>{
  const before=(await humans(shared.mf)).length;
  for(const phase of ["token","user"] as const){
    github.oversizedToken=phase==="token";
    github.oversizedUser=phase==="user";
    github.requests.length=0;
    try{
      const started=await begin();
      const result=await callback(shared.mf,new URLSearchParams({
        code:"fake-code",state:started.location!.searchParams.get("state")!
      }).toString(),cookieHeader(started.loginCookie!));
      assert.equal(result.status,502,phase+" oversized provider payload must fail closed");
      assert.equal(sessionCookie(result.headers),undefined,"oversized provider payload must not create a session");
      assert.equal((await humans(shared.mf)).length,before,"oversized provider payload cannot create a human");
    }finally{github.oversizedToken=false;github.oversizedUser=false;}
  }
});

test("callback_SlowGithubProvider_FailsClosedWithinDeadline",{timeout:40_000},async()=>{
  // A healthy but unresponsive upstream must not occupy a login Worker indefinitely.
  // Without a bounded provider fetch, this fixture eventually returns valid JSON
  // and signs the human in; an abort must instead fail closed before that point.
  const before=(await humans(shared.mf)).length;
  for(const phase of ["token","user"] as const){
    github.slowToken=phase==="token";
    github.slowUser=phase==="user";
    try{
      const started=await begin();
      const startedAt=performance.now();
      const result=await callback(shared.mf,new URLSearchParams({
        code:"fake-code",state:started.location!.searchParams.get("state")!
      }).toString(),cookieHeader(started.loginCookie!));
      const elapsed=performance.now()-startedAt;
      assert.equal(result.status,502,phase+" slow provider must not sign in");
      assert.ok(elapsed<5_200,phase+" must abort before the simulated 5.5s reply ("+elapsed+"ms)");
      assert.equal(sessionCookie(result.headers),undefined);
      assert.equal((await humans(shared.mf)).length,before);
    }finally{github.slowToken=false;github.slowUser=false;}
  }
});

test("callback_GithubExchangeFails_Returns502AndLogsCategory",{timeout:30_000},async()=>{
  github.requests.length=0;logs.length=0;
  const started=await begin();
  const result=await callback(shared.mf,"code=rejected-code&state="+started.location!.searchParams.get("state"),cookieHeader(started.loginCookie!));
  assert.equal(result.status,502);
  assert.equal(sessionCookie(result.headers),undefined);
  assert.ok(logs.some(x=>x.includes("github-login: token exchange failed")),JSON.stringify(logs));
  assert.ok(!logs.some(x=>x.includes("rejected-code")||x.includes("bad_verification_code")),"no codes or bodies in logs");
});

test("callback_GithubUserFails_Returns502WithoutSession",{timeout:30_000},async()=>{
  github.requests.length=0;logs.length=0;
  const before=(await humans(shared.mf)).length;
  github.userStatus=500;
  try{
    const started=await begin();
    const result=await callback(shared.mf,"code=fake-code&state="+started.location!.searchParams.get("state"),cookieHeader(started.loginCookie!));
    assert.equal(result.status,502);
    assert.equal(sessionCookie(result.headers),undefined);
    assert.equal((await humans(shared.mf)).length,before);
    assert.ok(logs.some(x=>x.includes("github-login: user fetch failed")),JSON.stringify(logs));
    assert.ok(!logs.some(x=>x.includes("upstream failure")),"no response bodies in logs");
  }finally{github.userStatus=200;}
});

test("callback_FrozenHuman_Returns403WithoutSession",{timeout:30_000},async()=>{
  const db=await shared.mf.getD1Database("db","weave-mailbox-login");
  await db.prepare("UPDATE humans SET frozen_at=updated_at WHERE subject='4242'").run();
  try{
    const started=await begin();
    const result=await callback(shared.mf,"code=fake-code&state="+started.location!.searchParams.get("state"),cookieHeader(started.loginCookie!));
    assert.equal(result.status,403);
    assert.match(result.body,/This GitHub identity is frozen; contact the operator/);
    assert.equal(sessionCookie(result.headers),undefined);
  }finally{await db.prepare("UPDATE humans SET frozen_at=NULL WHERE subject='4242'").run();}
});

test("callback_MailboxUnavailable_Returns503WithoutSession",{timeout:60_000},async()=>{
  const instance=await start({},{RECOVERY_READ_ONLY:{type:"text",value:"true"}});
  try{
    const started=await begin();
    const result=await callback(instance.mf,"code=fake-code&state="+started.location!.searchParams.get("state"),cookieHeader(started.loginCookie!));
    assert.equal(result.status,503);
    assert.match(result.body,/Sign-in temporarily unavailable/);
    assert.equal(sessionCookie(result.headers),undefined);
    assert.equal((await humans(instance.mf)).length,0);
  }finally{await stop(instance);}
});

test("callback_MailboxRpcThrows_Returns503AndLogsCategory",{timeout:60_000},async()=>{
  // The default export has no signIn method, so the RPC call itself fails.
  const instance=await start({MAILBOX:{type:"worker",worker:"weave-mailbox-login"}});
  logs.length=0;
  try{
    const started=await begin();
    const result=await callback(instance.mf,"code=fake-code&state="+started.location!.searchParams.get("state"),cookieHeader(started.loginCookie!));
    assert.equal(result.status,503);
    assert.equal(sessionCookie(result.headers),undefined);
    assert.ok(logs.some(x=>x.includes("human-directory: call failed")),JSON.stringify(logs));
  }finally{await stop(instance);}
});

test("allRoutes_MissingGithubSecret_Return503",{timeout:120_000},async()=>{
  const misconfigured:Record<string,Record<string,unknown>>={
    "missing secret":{GITHUB_CLIENT_SECRET:undefined},
    "missing client id":{GITHUB_CLIENT_ID:undefined},
    "malformed session key":{HUMAN_SESSION_KEY:{type:"text",value:randomBytes(16).toString("base64url")}},
    "missing mailbox":{MAILBOX:undefined}
  };
  for(const [label,override] of Object.entries(misconfigured)){
    const authEnv=Object.fromEntries(Object.entries(override).filter(([,v])=>v!==undefined));
    const storage=await mkdtemp(join(tmpdir(),"weave-auth-login-"));
    const base=options(storage,authEnv);
    for(const [name,value] of Object.entries(override))if(value===undefined)delete (base.workers[0].config.env as Record<string,unknown>)[name];
    const mf=new Miniflare(base);
    try{
      for(const path of ["/.well-known/oauth-authorization-server","/authorize?response_type=code",
        "/login/github/callback?code=fake-code&state=x","/oauth/token"]){
        const result=await capture(await mf.dispatchFetch(issuer+path,{redirect:"manual"}));
        assert.equal(result.status,503,label+" "+path);
        assert.equal(result.body,"Authorization not configured");
      }
    }finally{await mf.dispose();await rm(storage,{recursive:true,force:true});}
  }
});


test("boundedProviderJson_OversizedStreamWithStalledCancel_RejectsPromptly",{timeout:6_000},async()=>{
  // A provider can exceed the JSON byte limit and then stall during cancellation.
  // A bounded response must not await an untrusted ReadableStream.cancel() promise.
  let cancellationRequested=false;
  const body=new ReadableStream<Uint8Array>({
    start(controller){controller.enqueue(new Uint8Array(8_193));},
    cancel(){cancellationRequested=true;return new Promise<void>(()=>{});}
  });
  const started=performance.now();
  const result=await boundedProviderJson(new Response(body,{
    headers:{"Content-Type":"application/json"}
  }),8_192);
  assert.equal(result,null,"oversized provider data must be rejected");
  assert.equal(cancellationRequested,true,"discard the untrusted provider response");
  assert.ok(performance.now()-started<5_000,"stream cleanup must not defeat provider bounds");
});
