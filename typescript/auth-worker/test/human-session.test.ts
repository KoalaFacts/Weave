import assert from "node:assert/strict";
import {randomBytes,randomUUID} from "node:crypto";
import {test} from "node:test";
import {beginGithubLogin,completeGithubLogin,type GithubLoginEnv} from "../src/github-login.ts";
import {freshAuthentication,humanSessionCookie,issueHumanSession,readHumanSession,signedCookieValue} from "../src/human-session.ts";

const now=1_800_000_000_000;
const newKey=()=>randomBytes(32).toString("base64url");
function requestWith(setCookie:string):Request{
  return new Request("https://auth.weave.example/authorize",{headers:{Cookie:setCookie.split(";")[0]}});
}

test("readHumanSession_RoundTrip_CarriesAuthAt",async()=>{
  const key=newKey(),humanId=randomUUID();
  const cookie=await issueHumanSession(key,{humanId,login:"alice"},now);
  assert.deepEqual(await readHumanSession(key,requestWith(cookie),now+1000),
    {humanId,login:"alice",expiresAt:now+3_600_000,authAt:now});
});

test("readHumanSession_ThreeMemberLegacyShape_ReturnsNull",async()=>{
  const key=newKey();
  const legacy=await signedCookieValue(key,"human-session",{humanId:randomUUID(),login:"alice",expiresAt:now+3_600_000});
  assert.equal(await readHumanSession(key,requestWith(humanSessionCookie+"="+legacy),now),null);
  const late=await signedCookieValue(key,"human-session",{humanId:randomUUID(),login:"alice",expiresAt:now+1000,authAt:now+2000});
  assert.equal(await readHumanSession(key,requestWith(humanSessionCookie+"="+late),now),null,"authAt may not exceed expiresAt");
  const early=await signedCookieValue(key,"human-session",{humanId:randomUUID(),login:"alice",expiresAt:now+3_600_000,authAt:now-1});
  assert.equal(await readHumanSession(key,requestWith(humanSessionCookie+"="+early),now),null,"authAt may not predate the session lifetime");
  const owner=await signedCookieValue(key,"owner-action",{humanId:randomUUID(),login:"alice",expiresAt:now+3_600_000,authAt:now});
  assert.equal(await readHumanSession(key,requestWith(humanSessionCookie+"="+owner),now),null,"another purpose never verifies as a session");
});

test("freshAuthentication_Boundary",()=>{
  const at=(authAt:number)=>({humanId:randomUUID(),login:"alice",expiresAt:authAt+3_600_000,authAt});
  assert.equal(freshAuthentication(at(now-600_000),now),true);
  assert.equal(freshAuthentication(at(now-600_001),now),false);
  assert.equal(freshAuthentication(at(now+60_000),now),true);
  assert.equal(freshAuthentication(at(now+60_001),now),false);
});

test("readHumanSession_TamperedPayload_ReturnsNull",async()=>{
  const key=newKey();
  const cookie=await issueHumanSession(key,{humanId:randomUUID(),login:"alice"},now);
  const [name,value]=cookie.split(";")[0].split("=");
  const [,signature]=value.split(".");
  const forged=Buffer.from(JSON.stringify({humanId:randomUUID(),login:"mallory",expiresAt:now+3_600_000,authAt:now})).toString("base64url");
  assert.equal(await readHumanSession(key,requestWith(name+"="+forged+"."+signature),now),null);
});

test("readHumanSession_Expired_ReturnsNull",async()=>{
  const key=newKey();
  const cookie=await issueHumanSession(key,{humanId:randomUUID(),login:"alice"},now);
  assert.notEqual(await readHumanSession(key,requestWith(cookie),now+3_599_999),null);
  assert.equal(await readHumanSession(key,requestWith(cookie),now+3_600_000),null);
});

test("readHumanSession_OtherKey_ReturnsNull",async()=>{
  const cookie=await issueHumanSession(newKey(),{humanId:randomUUID(),login:"alice"},now);
  assert.equal(await readHumanSession(newKey(),requestWith(cookie),now),null);
});

test("readHumanSession_MissingOrDuplicateCookie_ReturnsNull",async()=>{
  const key=newKey();
  const cookie=(await issueHumanSession(key,{humanId:randomUUID(),login:"alice"},now)).split(";")[0];
  assert.equal(await readHumanSession(key,new Request("https://auth.weave.example/"),now),null);
  const doubled=new Request("https://auth.weave.example/",{headers:{Cookie:cookie+"; "+cookie}});
  assert.equal(await readHumanSession(key,doubled,now),null);
});

test("issueHumanSession_Cookie_HasHostPrefixAndFlags",async()=>{
  const cookie=await issueHumanSession(newKey(),{humanId:randomUUID(),login:"alice"},now);
  assert.match(cookie,/^__Host-weave_human=[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{43};/);
  const attributes=cookie.split(";").slice(1).map(x=>x.trim());
  for(const expected of ["Secure","HttpOnly","SameSite=Lax","Path=/","Max-Age=3600"])
    assert.ok(attributes.includes(expected),expected);
  assert.ok(!attributes.some(x=>/^domain=/i.test(x)),"a __Host- cookie must not carry Domain");
});

test("issueHumanSession_MalformedKey_Throws",async()=>{
  await assert.rejects(issueHumanSession("short",{humanId:randomUUID(),login:"alice"},now));
});

test("readHumanSession_LoginPurposeSignature_ReturnsNull",async()=>{
  const key=newKey();
  // Same key and the exact session shape, but signed for the GitHub login cookie.
  const value=await signedCookieValue(key,"github-login",{humanId:randomUUID(),login:"alice",expiresAt:now+3_600_000});
  assert.equal(await readHumanSession(key,requestWith(humanSessionCookie+"="+value),now),null);
});

test("completeGithubLogin_SessionPurposeSignature_Returns400WithoutGithubCalls",async()=>{
  const key=newKey(),state=randomBytes(32).toString("base64url");
  // Same key and the exact login shape, but signed as a human session.
  const value=await signedCookieValue(key,"human-session",{state,verifier:randomBytes(32).toString("base64url"),
    returnTo:"https://auth.weave.example/authorize",expiresAt:now+600_000});
  const calls:string[]=[];
  const original=globalThis.fetch;
  globalThis.fetch=async input=>{calls.push(String(input));return new Response(null,{status:500});};
  try{
    const env={OAUTH_ISSUER:"https://auth.weave.example",GITHUB_CLIENT_ID:"Iv1.test",GITHUB_CLIENT_SECRET:"secret",
      HUMAN_SESSION_KEY:key,MAILBOX:{} as GithubLoginEnv["MAILBOX"]};
    const response=await completeGithubLogin(env,new Request("https://auth.weave.example/login/github/callback?code=c&state="+state,
      {headers:{Cookie:"__Host-weave_login="+value}}),now);
    assert.equal(response.status,400);
    assert.equal(calls.length,0);
  }finally{globalThis.fetch=original;}
});

test("beginGithubLogin_StateTooLongForLoginCookie_Returns400WithoutRedirect",async()=>{
  const env={OAUTH_ISSUER:"https://auth.weave.example",GITHUB_CLIENT_ID:"Iv1.test-client-id",HUMAN_SESSION_KEY:newKey()};
  const returnTo=(state:string)=>{
    const url=new URL("https://auth.weave.example/authorize");
    url.searchParams.set("client_id","https://client.example/oauth.json");
    url.searchParams.set("state",state);
    return url;
  };
  const accepted=await beginGithubLogin(env,returnTo("state-"+randomUUID()),now);
  assert.equal(accepted.status,302,"an ordinary request still starts GitHub sign-in");
  const cookie=accepted.headers.get("set-cookie")!.split(";")[0];
  assert.ok(cookie.length-"__Host-weave_login=".length<=2048);

  const refused=await beginGithubLogin(env,returnTo("x".repeat(1500)),now);
  assert.equal(refused.status,400);
  assert.equal(refused.headers.get("location"),null,"never send the browser to GitHub with a cookie that cannot be read back");
  assert.equal(refused.headers.get("set-cookie"),null);
  assert.match(await refused.text(),/Authorization request too long/);
});

const loginEnv=()=>({OAUTH_ISSUER:"https://auth.weave.example",GITHUB_CLIENT_ID:"Iv1.test-client-id",HUMAN_SESSION_KEY:newKey()});

test("beginGithubLogin_Reauthenticate_AddsSelectAccountAndLogin",async()=>{
  const response=await beginGithubLogin(loginEnv(),new URL("https://auth.weave.example/account"),now,{reauthenticate:{login:"octo-human"}});
  assert.equal(response.status,302);
  const location=new URL(response.headers.get("location")!);
  assert.equal(location.searchParams.get("prompt"),"select_account");
  assert.equal(location.searchParams.get("login"),"octo-human");
  assert.equal(location.searchParams.get("scope"),"read:user");
  const plain=new URL((await beginGithubLogin(loginEnv(),new URL("https://auth.weave.example/authorize"),now)).headers.get("location")!);
  assert.equal(plain.searchParams.has("prompt"),false);
  assert.equal(plain.searchParams.has("login"),false);
});

test("beginGithubLogin_AccountReturnTo_Allowed_QueryRefused",async()=>{
  const go=(path:string)=>beginGithubLogin(loginEnv(),new URL("https://auth.weave.example"+path),now);
  assert.equal((await go("/account")).status,302);
  assert.equal((await go("/account?x=1")).status,400);
  assert.equal((await go("/accounts")).status,400);
});
