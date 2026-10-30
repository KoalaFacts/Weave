import assert from "node:assert/strict";
import {test} from "node:test";
import {createChallenge,validChallenge,readConsent} from "../src/challenge.ts";
import {consentPage} from "../src/consent-page.ts";
test("256-bit canonical challenges are unique and reject alternate encodings",()=>{
  const nonces=Array.from({length:80},createChallenge);
  assert.equal(new Set(nonces).size,80);
  for(const nonce of nonces)assert.equal(validChallenge(nonce),true);
  for(const bad of [undefined,"","A".repeat(42),"*".repeat(43),"A".repeat(44)])
    assert.equal(validChallenge(bad),false);
});
test("consent body rejects duplicates, unexpected fields and unbounded input",async()=>{
  for(const value of ["handle=A&handle=B&decision=approve",
    "handle=A&decision=approve&accountId=forged",
    "handle=A&decision=approve&proof="+"x".repeat(9000)]){
    const req=new Request("https://auth.example/authorize",{method:"POST",
      headers:{"Content-Type":"application/x-www-form-urlencoded"},body:value});
    assert.equal(await readConsent(req),null);
  }
  const req=new Request("https://auth.example/authorize",{method:"POST",
    headers:{"Content-Type":"application/x-www-form-urlencoded"},
    body:new URLSearchParams({handle:createChallenge(),decision:"approve",proof:"wop1.AA.BB"})});
  assert.equal((await readConsent(req))?.get("decision"),"approve");
});
test("consent renders escaped client metadata and explicit decisions without control",async()=>{
  const response=consentPage({
    clientName:"<script>alert(1)</script>",
    clientDomain:'evil.example"><img src=x>',
    redirectHost:"127.0.0.1",redirectIsLoopback:true,
    scope:['weave:messages.send"><svg onload=x>']
  } as Parameters<typeof consentPage>[0],createChallenge(),
    "https://demo.weave-389.workers.dev/account-proof?challenge="+createChallenge());
  const html=await response.text();
  assert.equal(response.status,200);
  assert.doesNotMatch(html,/<script>alert\(1\)<\/script>|<svg onload=x>|<img src=x>/);
  assert.match(html,/&#60;script&#62;/);assert.match(html,/&#60;svg/);
  assert.match(html,/name="decision" value="approve"/);
  assert.match(html,/name="decision" value="deny"/);
  assert.doesNotMatch(html,/X-Weave-Account-Control/);
  assert.match(response.headers.get("content-security-policy")??"",/frame-ancestors 'none'/);
  assert.equal(response.headers.get("referrer-policy"),"no-referrer");
});
