import assert from "node:assert/strict";
import {randomBytes,randomUUID} from "node:crypto";
import {test} from "node:test";
import type {OwnerOverview} from "../../mailbox-worker/src/owner-actions.ts";
import type {HumanSession} from "../src/human-session.ts";
import {ownerFormTokens,ownerPage,ownerResultText,recoveryCodePage} from "../src/owner-page.ts";

const now=1_800_000_000_000;
const kid=()=>randomBytes(32).toString("base64url");
const human:HumanSession={humanId:randomUUID(),login:"alice",expiresAt:now+3_600_000,authAt:now};
const iso=(ms:number)=>new Date(ms).toISOString();
const base=(over:Partial<Extract<OwnerOverview,{frozen:false}>>={}):OwnerOverview=>({frozen:false,bindingRequests:[],agents:[],...over});
const html=(response:Response)=>response.text();

test("ownerPage_EscapesSelfReportedNameAndShowsFingerprints",async()=>{
  const requesting=kid(),live=kid(),label='<img src=x onerror=alert(1)>';
  const requestId=randomUUID(),agentId=randomUUID();
  const overview=base({bindingRequests:[{requestId,agentId,displayName:'<script>alert(1)</script>',requestingKid:requesting,
    liveKeys:[{kid:live,label:null,createdAt:iso(now),lastUsedAt:null,coolingUntil:iso(now+3_600_000)}],keysDigest:kid(),createdAt:iso(now),expiresAt:iso(now+86_400_000)}],
    agents:[{agentId:randomUUID(),displayName:null,keys:[{kid:kid(),label,createdAt:iso(now),lastUsedAt:null,coolingUntil:null}],keyRequests:[]}]});
  const tokens=new Map([["confirm-binding:"+requestId,"tok-confirm"],["reject-binding:"+requestId,"tok-reject"]]);
  const response=ownerPage(overview,human,tokens,null,now);
  assert.equal(response.status,200);
  assert.equal(response.headers.get("cache-control"),"no-store");
  assert.equal(response.headers.get("x-frame-options"),"DENY");
  assert.match(response.headers.get("content-security-policy")!,/form-action 'self'/);
  const body=await html(response);
  assert.ok(!body.includes("<script>")&&!body.includes("<img"),"agent-supplied text must be escaped");
  assert.ok(body.includes("&#60;script&#62;"));
  assert.ok(body.includes("self-reported, not verified"));
  assert.ok(body.includes("<strong>"+requesting.slice(0,8)+"</strong>"+requesting.slice(8)));
  assert.ok(body.includes(live)||body.includes(live.slice(8)));
  assert.ok(body.includes("still in its 24 h cooling window"));
  assert.ok(body.includes("Signed in as @alice"));
  assert.ok(body.includes('value="tok-confirm"')&&body.includes('value="tok-reject"'));
  assert.ok(body.includes("Confirming or revoking needs a GitHub sign-in in the last 10 minutes"));
});

test("ownerPage_Frozen_NoForms",async()=>{
  const body=await html(ownerPage({frozen:true},human,new Map([["x","y"]]),"done",now));
  assert.ok(!body.includes("<form"));
  assert.ok(body.includes("frozen"));
});

test("ownerPage_UnknownResult_Ignored",async()=>{
  for(const result of ["<b>pwned</b>","__proto__","constructor","toString",""]){
    const body=await html(ownerPage(base(),human,new Map(),result,now));
    assert.ok(!body.includes('role="status"'),"result "+JSON.stringify(result)+" must not render");
  }
  assert.ok((await html(ownerPage(base(),human,new Map(),"agent-limit",now))).includes(ownerResultText["agent-limit"]));
  assert.equal(Object.keys(ownerResultText).length,14);
});

test("ownerPage_StaleSession_SaysSignInAgain",async()=>{
  const stale={...human,authAt:now-600_001};
  assert.ok((await html(ownerPage(base(),stale,new Map(),null,now))).includes("asked to sign in again"));
});

test("ownerPage_RecoveryRequest_LabelledUnendorsed",async()=>{
  const agentId=randomUUID(),requestId=randomUUID(),endorser=kid();
  const request=(endorsedBy:string|null)=>({requestId,kid:kid(),label:null,endorsedBy,createdAt:iso(now),expiresAt:iso(now+1000)});
  const none=await html(ownerPage(base({agents:[{agentId,displayName:"a",keys:[],keyRequests:[request(null)]}]}),human,new Map(),null,now));
  assert.ok(none.includes("recovery request - no existing key signed it"));
  const endorsed=await html(ownerPage(base({agents:[{agentId,displayName:"a",keys:[],keyRequests:[request(endorser)]}]}),human,new Map(),null,now));
  assert.ok(!endorsed.includes("recovery request")&&endorsed.includes("endorsed by"));
});

test("ownerFormTokens_CoversEveryFormTheOverviewRenders",async()=>{
  const signingKey=randomBytes(32).toString("base64url");
  const requestId=randomUUID(),agentId=randomUUID(),keyId=kid(),keyRequestId=randomUUID();
  const overview=base({bindingRequests:[{requestId,agentId:randomUUID(),displayName:null,requestingKid:kid(),liveKeys:[],keysDigest:kid(),createdAt:iso(now),expiresAt:iso(now+1)}],
    agents:[{agentId,displayName:"a",keys:[{kid:keyId,label:null,createdAt:iso(now),lastUsedAt:null,coolingUntil:null}],
      keyRequests:[{requestId:keyRequestId,kid:kid(),label:null,endorsedBy:keyId,createdAt:iso(now),expiresAt:iso(now+1)}]}]});
  const tokens=await ownerFormTokens(signingKey,human.humanId,overview,now);
  assert.deepEqual([...tokens.keys()].sort(),["confirm-binding:"+requestId,"confirm-key:"+keyRequestId,"issue-recovery-code:"+agentId,
    "reject-binding:"+requestId,"reject-key:"+keyRequestId,"revoke-key:"+agentId+":"+keyId].sort());
  const body=await html(ownerPage(overview,human,tokens,null,now));
  assert.equal(body.match(/<form /g)!.length,tokens.size);
  assert.equal((await ownerFormTokens(signingKey,human.humanId,{frozen:true},now)).size,0);
});

test("recoveryCodePage_ShowsCodeNoStoreAndRedeemInstructions",async()=>{
  const response=recoveryCodePage("agent-1","code<>1","2026-11-01T00:10:00.000Z");
  assert.equal(response.status,200);
  assert.equal(response.headers.get("cache-control"),"no-store");
  const body=await html(response);
  assert.ok(body.includes("code&#60;&#62;1")&&body.includes("/v1/key-requests")&&body.includes("DPoP")&&body.includes("2026-11-01T00:10:00.000Z"));
});
