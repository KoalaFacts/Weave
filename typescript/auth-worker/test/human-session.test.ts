import assert from "node:assert/strict";
import {randomBytes,randomUUID} from "node:crypto";
import {test} from "node:test";
import {completeGithubLogin,type GithubLoginEnv} from "../src/github-login.ts";
import {humanSessionCookie,issueHumanSession,readHumanSession,signedCookieValue} from "../src/human-session.ts";

const now=1_800_000_000_000;
const newKey=()=>randomBytes(32).toString("base64url");
function requestWith(setCookie:string):Request{
  return new Request("https://auth.weave.example/authorize",{headers:{Cookie:setCookie.split(";")[0]}});
}

test("readHumanSession_RoundTrip_ReturnsSession",async()=>{
  const key=newKey(),humanId=randomUUID();
  const cookie=await issueHumanSession(key,{humanId,login:"alice"},now);
  assert.deepEqual(await readHumanSession(key,requestWith(cookie),now+1000),
    {humanId,login:"alice",expiresAt:now+3_600_000});
});

test("readHumanSession_TamperedPayload_ReturnsNull",async()=>{
  const key=newKey();
  const cookie=await issueHumanSession(key,{humanId:randomUUID(),login:"alice"},now);
  const [name,value]=cookie.split(";")[0].split("=");
  const [,signature]=value.split(".");
  const forged=Buffer.from(JSON.stringify({humanId:randomUUID(),login:"mallory",expiresAt:now+3_600_000})).toString("base64url");
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
