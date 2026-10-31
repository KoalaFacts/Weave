import assert from "node:assert/strict";
import {randomUUID} from "node:crypto";
import {test} from "node:test";
import {createChallenge,validChallenge,readConsent} from "../src/challenge.ts";
import {consentPage} from "../src/consent-page.ts";

const consentRequest=(body:string)=>new Request("https://auth.example/authorize",{method:"POST",
  headers:{"Content-Type":"application/x-www-form-urlencoded"},body});
const details={
  clientName:"<script>alert(1)</script>",
  clientDomain:'evil.example"><img src=x>',
  redirectHost:"127.0.0.1",redirectIsLoopback:true,
  scope:['weave:messages.send"><svg onload=x>']
} as Parameters<typeof consentPage>[0];

test("256-bit canonical challenges are unique and reject alternate encodings",()=>{
  const nonces=Array.from({length:80},createChallenge);
  assert.equal(new Set(nonces).size,80);
  for(const nonce of nonces)assert.equal(validChallenge(nonce),true);
  for(const bad of [undefined,"","A".repeat(42),"*".repeat(43),"A".repeat(44)])
    assert.equal(validChallenge(bad),false);
});

test("readConsent_InvalidBodies_ReturnNull",async()=>{
  const handle=createChallenge(),account=randomUUID();
  for(const value of [
    "handle=A&handle=B&decision=approve&agent=new&agentName=x",
    "handle="+handle+"&decision=approve&agent="+account+"&accountId=forged",
    "handle="+handle+"&decision=approve&proof=AA.BB&agent="+account,
    "handle="+handle+"&decision=approve&agent="+account+"&agent="+randomUUID(),
    "handle="+handle+"&decision=approve&agent="+account+"&agentName=Claude",
    "handle="+handle+"&decision=approve&agentName=Claude",
    "handle="+handle+"&decision=approve&agent=new&agentName=a&agentName=b",
    "handle="+handle+"&decision=approve",
    "handle="+handle+"&decision=approve&agent=not-a-uuid",
    "handle="+handle+"&decision=approve&agent=new&agentName="+"x".repeat(9000)
  ])assert.equal(await readConsent(consentRequest(value)),null,value.slice(0,120));
});

test("readConsent_ValidChoices_ReturnForm",async()=>{
  const handle=createChallenge(),account=randomUUID();
  const created=await readConsent(consentRequest(new URLSearchParams(
    {handle,decision:"approve",agent:"new",agentName:"Claude Code"}).toString()));
  assert.equal(created?.get("agentName"),"Claude Code");
  const existing=await readConsent(consentRequest(new URLSearchParams({handle,decision:"approve",agent:account}).toString()));
  assert.equal(existing?.get("agent"),account);
  const deny=await readConsent(consentRequest(new URLSearchParams({handle,decision:"deny"}).toString()));
  assert.equal(deny?.get("decision"),"deny");
});

test("consentPage_OwnerWithAgents_ListsEscapedAgentsAndSignedInLogin",async()=>{
  const first=randomUUID(),second=randomUUID();
  const response=consentPage(details,createChallenge(),{login:"alice"},
    [{accountId:first,displayName:"<b>Claude</b>"},{accountId:second,displayName:null}],"Claude Code");
  const html=await response.text();
  assert.equal(response.status,200);
  assert.match(html,/Signed in as @alice/);
  assert.match(html,new RegExp('name="agent" value="'+first+'"'));
  assert.match(html,new RegExp('name="agent" value="'+second+'"'));
  assert.match(html,/&#60;b&#62;Claude&#60;\/b&#62;/);
  assert.doesNotMatch(html,/<b>Claude<\/b>/);
  assert.match(html,/name="agent" value="new"/);
  assert.match(html,/name="agentName"[^>]*value="Claude Code"/);
  assert.match(html,/GitHub login is not shown to other agents/);
  assert.doesNotMatch(html,/proof|control/i);
});

test("consentPage_EachForm_SubmitsOnlyFieldsReadConsentAccepts",async()=>{
  const html=await consentPage(details,createChallenge(),{login:"alice"},
    [{accountId:randomUUID(),displayName:"A"}],"Claude Code").text();
  const forms=html.match(/<form[\s\S]*?<\/form>/g)??[];
  assert.ok(forms.length>=2);
  for(const form of forms){
    const names=new Set([...form.matchAll(/name="([^"]+)"/g)].map(x=>x[1]));
    assert.ok(names.has("handle")&&names.has("decision"));
    // A browser submits every text input, so agentName may only live beside agent=new.
    if(names.has("agentName"))assert.match(form,/name="agent" value="new"/);
    for(const name of names)assert.ok(["handle","decision","agent","agentName"].includes(name),name);
  }
});

test("consentPage_UntrustedClient_EscapesMetadataAndKeepsHeaders",async()=>{
  const response=consentPage(details,createChallenge(),{login:"alice"},[],"Claude Code");
  const html=await response.text();
  assert.doesNotMatch(html,/<script>alert\(1\)<\/script>|<svg onload=x>|<img src=x>/);
  assert.match(html,/&#60;script&#62;/);assert.match(html,/&#60;svg/);
  assert.match(html,/name="decision" value="approve"/);
  assert.match(html,/name="decision" value="deny"/);
  assert.match(response.headers.get("content-security-policy")??"",/frame-ancestors 'none'/);
  assert.match(response.headers.get("content-security-policy")??"",/form-action 'self'/);
  assert.equal(response.headers.get("referrer-policy"),"no-referrer");
  assert.equal(response.headers.get("x-frame-options"),"DENY");
  const unverified=await consentPage({...details,clientDomain:undefined},createChallenge(),{login:"alice"},[],"x").text();
  assert.match(unverified,/Self-registered client; its name is not independently verified/);
});


test("readConsent_StalledFormStream_FailsClosedWithinDeadline",{timeout:7_000},async()=>{
  // An untrusted POST may send only part of a form and never finish the body.
  // A byte cap alone cannot bound this pending read, so it must fail closed.
  const partial=new TextEncoder().encode("handle="+createChallenge()+"&decision=approve&agent=new&agentName=");
  const stream=new ReadableStream<Uint8Array>({
    start(controller){controller.enqueue(partial);},
    cancel(){/* Simulated client stops sending; close after cancellation. */}
  });
  const request=new Request("https://auth.example/authorize",{
    method:"POST",headers:{"Content-Type":"application/x-www-form-urlencoded"},body:stream,duplex:"half"
  } as RequestInit);
  const start=performance.now();
  assert.equal(await readConsent(request),null,"a stalled consent body cannot be authorized");
  assert.ok(performance.now()-start<5_500,"pending request body must be bounded");
});

test("readConsent_CancelNeverSettles_StillReturnsAtDeadline",{timeout:6_000},async()=>{
  // A stalled upload can also supply a cancel() that never resolves. The timeout
  // must bound the whole operation, including stream cleanup, not just read().
  const partial=new TextEncoder().encode("handle="+createChallenge()+"&decision=approve&agent=new&agentName=");
  const stream=new ReadableStream<Uint8Array>({
    start(controller){controller.enqueue(partial);},
    cancel(){return new Promise<void>(()=>{});}
  });
  const request=new Request("https://auth.example/authorize",{
    method:"POST",headers:{"Content-Type":"application/x-www-form-urlencoded"},body:stream,duplex:"half"
  } as RequestInit);
  const start=performance.now();
  assert.equal(await readConsent(request),null);
  assert.ok(performance.now()-start<5_500,"cleanup must not defeat the upload deadline");
});
