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
const base=(over:Partial<Extract<OwnerOverview,{frozen:false}>>={}):OwnerOverview=>({frozen:false,bindingRequests:[],agents:[],externalActions:[],...over});
const html=(response:Response)=>response.text();

test("ownerPage_EscapesSelfReportedNameAndShowsFingerprints",async()=>{
  const requesting=kid(),live=kid(),label='<img src=x onerror=alert(1)>';
  const requestId=randomUUID(),agentId=randomUUID();
  const overview=base({bindingRequests:[{requestId,agentId,displayName:'<script>alert(1)</script>',requestingKid:requesting,
    liveKeys:[{kid:live,label:null,createdAt:iso(now),lastUsedAt:null,coolingUntil:iso(now+3_600_000)}],keysDigest:kid(),createdAt:iso(now),expiresAt:iso(now+86_400_000)}],
    agents:[{agentId:randomUUID(),displayName:null,keys:[{kid:kid(),label,createdAt:iso(now),lastUsedAt:null,coolingUntil:null}],keyRequests:[],grants:[],addresses:[],revokedGrantIds:[]}]});
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
  assert.ok(body.includes("<li><code><strong>"+live.slice(0,8)+"</strong>"+live.slice(8)+"</code> - still in its 24 h cooling window</li>"));
  assert.ok(body.includes("still in its 24 h cooling window"));
  assert.ok(body.includes("Signed in as @alice"));
  assert.ok(body.includes('value="tok-confirm"')&&body.includes('value="tok-reject"'));
  assert.ok(body.includes("Confirming, approving an action, revoking a key or issuing a recovery code needs a GitHub sign-in in the last 10 minutes"));
  assert.ok(body.includes("Revoking a connected client's access does not."));
});

const agent=(over:Partial<Extract<OwnerOverview,{frozen:false}>["agents"][number]>={})=>({agentId:randomUUID(),displayName:"a",keys:[],keyRequests:[],
  grants:[],addresses:[],revokedGrantIds:[],...over});

test("ownerPage_ConnectedClients_EscapedUnverifiedWithRevoke",async()=>{
  const hostile=randomUUID(),unnamed=randomUUID(),revoked=randomUUID();
  const overview=base({agents:[agent({grants:[
    {grantId:hostile,clientId:'javascript:alert(1)//"><script>x</script>',clientName:'<script>alert(1)</script><a href="javascript:alert(2)">x</a>',createdAt:iso(now)},
    {grantId:unnamed,clientId:"https://client.example/cimd.json",clientName:null,createdAt:iso(now-1000)}],revokedGrantIds:[revoked]})]});
  const tokens=new Map([["revoke-grant:"+hostile,"tok-hostile"],["revoke-grant:"+unnamed,"tok-unnamed"],["revoke-grant:"+revoked,"tok-revoked"]]);
  const body=await html(ownerPage(overview,human,tokens,null,now));
  assert.ok(body.includes("<h4>Connected clients</h4>"));
  assert.ok(!body.includes("<script>")&&!body.includes("<a ")&&!body.includes('href="'),"client-supplied text must not become markup or a link");
  assert.ok(body.includes("&#60;script&#62;alert(1)&#60;/script&#62;&#60;a href=&#34;javascript:alert(2)&#34;&#62;x&#60;/a&#62; (self-reported, not verified)"));
  assert.ok(body.includes("<code>javascript:alert(1)//&#34;&#62;&#60;script&#62;x&#60;/script&#62;</code>"));
  assert.ok(body.includes("Unnamed client")&&body.includes("<code>https://client.example/cimd.json</code>"));
  assert.ok(body.includes('value="tok-hostile"><button type="submit">Revoke access</button>')&&body.includes('value="tok-unnamed"'));
  assert.ok(!body.includes("tok-revoked")&&!body.includes(revoked),"revoked grant IDs are for clean-up only, never rendered");
  const none=await html(ownerPage(base({agents:[agent()]}),human,new Map(),null,now));
  assert.ok(none.includes("No connected clients."));
});

test("ownerPage_Addresses_Listed",async()=>{
  const publicId=randomUUID(),privateId=randomUUID();
  const overview=base({agents:[agent({addresses:[
    {addressId:publicId,label:'<img src=x onerror=alert(1)>',isPublic:true,hasAgentCard:true,createdAt:iso(now)},
    {addressId:privateId,label:null,isPublic:false,hasAgentCard:false,createdAt:iso(now+1000)}]})]});
  const body=await html(ownerPage(overview,human,new Map(),null,now));
  assert.ok(body.includes("<h4>Addresses</h4>"));
  assert.ok(!body.includes("<img"));
  assert.ok(body.includes("<code>"+publicId+"</code> label &#60;img src=x onerror=alert(1)&#62; (self-reported, not verified); public; has an agent card; created "+iso(now)));
  assert.ok(body.includes("<code>"+privateId+"</code>; private; no agent card; created "+iso(now+1000)));
  assert.ok((await html(ownerPage(base({agents:[agent()]}),human,new Map(),null,now))).includes("No addresses."));
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
  assert.equal(Object.keys(ownerResultText).length,18);
  assert.ok((await html(ownerPage(base(),human,new Map(),"cleanup-pending",now))).includes("Access is revoked; token clean-up will be retried on your next visit."));
  assert.ok((await html(ownerPage(base(),human,new Map(),"cleanup-complete",now))).includes("Access is revoked and token clean-up is complete."));
  assert.ok((await html(ownerPage(base(),human,new Map(),"form-expired",now))).includes("This page expired. Reload it and try again."));
});

test("ownerPage_StaleSession_SaysSignInAgain",async()=>{
  const stale={...human,authAt:now-600_001};
  assert.ok((await html(ownerPage(base(),stale,new Map(),null,now))).includes("asked to sign in again"));
});

test("ownerFormTokens_CoversEveryFormTheOverviewRenders",async()=>{
  const signingKey=randomBytes(32).toString("base64url");
  const requestId=randomUUID(),agentId=randomUUID(),keyId=kid(),keyRequestId=randomUUID(),grantId=randomUUID();
  const overview=base({bindingRequests:[{requestId,agentId:randomUUID(),displayName:null,requestingKid:kid(),liveKeys:[],keysDigest:kid(),createdAt:iso(now),expiresAt:iso(now+1)}],
    agents:[{agentId,displayName:"a",keys:[{kid:keyId,label:null,createdAt:iso(now),lastUsedAt:null,coolingUntil:null}],
      keyRequests:[{requestId:keyRequestId,kid:kid(),label:null,endorsedBy:keyId,createdAt:iso(now),expiresAt:iso(now+1)}],
      grants:[{grantId,clientId:"https://client.example/cimd.json",clientName:"c",createdAt:iso(now)}],addresses:[],revokedGrantIds:[randomUUID()]}]});
  const tokens=await ownerFormTokens(signingKey,human.humanId,overview,now);
  assert.deepEqual([...tokens.keys()].sort(),["confirm-binding:"+requestId,"confirm-key:"+keyRequestId,"issue-recovery-code:"+agentId,
    "reject-binding:"+requestId,"reject-key:"+keyRequestId,"revoke-grant:"+grantId,"revoke-key:"+agentId+":"+keyId].sort());
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

test("ownerPage_PendingAction_ShowsEscapedContentVerifiedAccountAndBoundForms",async()=>{
  const actionId=randomUUID(),agentId=randomUUID(),planDigest=kid();
  const overview=base({agents:[agent({agentId,displayName:"Publisher"})],externalActions:[{actionId,agentId,provider:"test-http",operation:"publish_text",
    content:'Episode 12\n<script>alert(1)</script>',credentialRef:"linkedin-main",credentialRevision:"rev-7",targetAccount:"urn:li:person:alpha",
    targetDisplay:"Desk <b>Alpha</b>",planDigest,createdAt:iso(now),expiresAt:iso(now+3_600_000)}]});
  const tokens=await ownerFormTokens(randomBytes(32).toString("base64url"),human.humanId,overview,now);
  assert.ok(tokens.has("confirm-action:"+actionId)&&tokens.has("reject-action:"+actionId));
  const body=await html(ownerPage(overview,human,tokens,null,now));
  assert.ok(!body.includes("<script>")&&!body.includes("<b>"),"agent and provider text must be escaped");
  assert.ok(body.includes("<pre>Episode 12\n&#60;script&#62;alert(1)&#60;/script&#62;</pre>"));
  assert.ok(body.includes("As Desk &#60;b&#62;Alpha&#60;/b&#62; (<code>urn:li:person:alpha</code>), verified by test-http; credential <code>linkedin-main@rev-7</code>"));
  assert.ok(body.includes("Publisher (self-reported, not verified) wants to publish_text on test-http"));
  assert.ok(body.includes("Approve and allow sending once")&&body.includes('value="'+tokens.get("confirm-action:"+actionId)+'"'));
  const none=await html(ownerPage(base(),human,new Map(),null,now));
  assert.ok(none.includes("No agent is waiting for an action to be approved."));
});
