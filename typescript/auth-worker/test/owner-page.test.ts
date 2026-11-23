import assert from "node:assert/strict";
import {randomBytes,randomUUID} from "node:crypto";
import {test} from "node:test";
import type {OwnerAppGrant,OwnerOverview} from "../../mailbox-worker/src/owner-actions.ts";
import type {HumanSession} from "../src/human-session.ts";
import {actionPage,ownerFormTokens,ownerPage,ownerResultText,recoveryCodePage,type SettledActionView} from "../src/owner-page.ts";
import {publishingAdapters} from "../../mailbox-worker/src/external-providers.ts";
import {ownerConnector,type OwnerConnector} from "../../mailbox-worker/src/publishing.ts";
/** The platforms the mailbox offers, as its own adapters describe them. */
const connectors=publishingAdapters.map(ownerConnector);

const now=1_800_000_000_000;
const kid=()=>randomBytes(32).toString("base64url");
const human:HumanSession={humanId:randomUUID(),login:"alice",expiresAt:now+3_600_000,authAt:now};
const iso=(ms:number)=>new Date(ms).toISOString();
const base=(over:Partial<Extract<OwnerOverview,{frozen:false}>>={}):OwnerOverview=>({frozen:false,bindingRequests:[],agents:[],nextAgents:null,externalActions:[],moreExternalActions:false,recentActions:[],moreRecentActions:false,revokedGrants:[],ownerGrants:[],revokedOwnerGrantIds:[],connectors:[],...over});
const html=(response:Response)=>response.text();

test("ownerPage_EscapesSelfReportedNameAndShowsFingerprints",async()=>{
  const requesting=kid(),live=kid(),label='<img src=x onerror=alert(1)>';
  const requestId=randomUUID(),agentId=randomUUID();
  const overview=base({bindingRequests:[{requestId,agentId,displayName:'<script>alert(1)</script>',requestingKid:requesting,
    liveKeys:[{kid:live,label:null,createdAt:iso(now),lastUsedAt:null,coolingUntil:iso(now+3_600_000)}],keysDigest:kid(),createdAt:iso(now),expiresAt:iso(now+86_400_000)}],
    agents:[{agentId:randomUUID(),displayName:null,keys:[{kid:kid(),label,createdAt:iso(now),lastUsedAt:null,coolingUntil:null}],keyRequests:[],grants:[],addresses:[],credentials:[]}]});
  const tokens=new Map([["confirm-binding:"+requestId,"tok-confirm"],["reject-binding:"+requestId,"tok-reject"]]);
  const response=ownerPage(overview,human,tokens,null,now);
  assert.equal(response.status,200);
  assert.equal(response.headers.get("cache-control"),"no-store");
  assert.equal(response.headers.get("referrer-policy"),"same-origin");
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
  assert.ok(body.includes("Confirming, approving an action, connecting an account, revoking a key or issuing a recovery code needs a GitHub sign-in in the last 10 minutes"));
  assert.ok(body.includes("Revoking a connected client's access or disconnecting an account does not."));
});

const agent=(over:Partial<Extract<OwnerOverview,{frozen:false}>["agents"][number]>={})=>({agentId:randomUUID(),displayName:"a",keys:[],keyRequests:[],
  grants:[],addresses:[],credentials:[],...over});

test("ownerPage_ConnectedClients_EscapedUnverifiedWithRevoke",async()=>{
  const hostile=randomUUID(),unnamed=randomUUID(),revoked=randomUUID();
  const overview=base({agents:[agent({grants:[
    {grantId:hostile,clientId:'javascript:alert(1)//"><script>x</script>',clientName:'<script>alert(1)</script><a href="javascript:alert(2)">x</a>',createdAt:iso(now)},
    {grantId:unnamed,clientId:"https://client.example/cimd.json",clientName:null,createdAt:iso(now-1000)}]})],revokedGrants:[{agentId:randomUUID(),revokedGrantIds:[revoked]}]});
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
  assert.equal(Object.keys(ownerResultText).length,26);
  assert.ok((await html(ownerPage(base(),human,new Map(),"account-changed",now))).includes("so it can no longer be approved. Reject it; your agent can propose it again."));
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
      grants:[{grantId,clientId:"https://client.example/cimd.json",clientName:"c",createdAt:iso(now)}],addresses:[],credentials:[]}],revokedGrants:[{agentId,revokedGrantIds:[randomUUID()]}],connectors});
  const tokens=await ownerFormTokens(signingKey,human.humanId,overview,now,false);
  assert.deepEqual([...tokens.keys()].sort(),["confirm-binding:"+requestId,"confirm-key:"+keyRequestId,...connectors.map(c=>"connect-account:"+agentId+":"+c.provider),"issue-recovery-code:"+agentId,
    "reject-binding:"+requestId,"reject-key:"+keyRequestId,"revoke-grant:"+grantId,"revoke-key:"+agentId+":"+keyId].sort());
  const body=await html(ownerPage(overview,human,tokens,null,now));
  assert.equal(body.match(/<form /g)!.length,tokens.size);
  assert.equal((await ownerFormTokens(signingKey,human.humanId,{frozen:true},now,false)).size,0);
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
  const overview=base({agents:[agent({agentId,displayName:"Publisher"})],externalActions:[{actionId,agentId,agentDisplayName:"Publisher",provider:"test-http",operation:"publish_text",
    content:'Episode 12\n<script>alert(1)</script>',credentialRef:"linkedin-main",credentialRevision:"rev-7",targetAccount:"urn:li:person:alpha",
    targetDisplay:"Desk <b>Alpha</b>",planDigest,createdAt:iso(now),expiresAt:iso(now+3_600_000),media:[],accountChanged:false}]});
  const tokens=await ownerFormTokens(randomBytes(32).toString("base64url"),human.humanId,overview,now,false);
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

test("ownerPage_ActionWhoseAccountChanged_OffersOnlyRejectWithTheReason",async()=>{
  const actionId=randomUUID(),agentId=randomUUID();
  const action={actionId,agentId,agentDisplayName:"Publisher",provider:"test-http",operation:"publish_text",content:"Episode 12",credentialRef:"linkedin-main",credentialRevision:"rev-7",
    targetAccount:"urn:li:person:alpha",targetDisplay:"Desk Alpha",planDigest:kid(),createdAt:iso(now),expiresAt:iso(now+3_600_000),media:[],accountChanged:true};
  const overview=base({agents:[agent({agentId,displayName:"Publisher"})],externalActions:[action]});
  const tokens=await ownerFormTokens(randomBytes(32).toString("base64url"),human.humanId,overview,now,false);
  assert.equal(tokens.has("confirm-action:"+actionId),false,"no approval token for a plan that can never be approved");
  assert.ok(tokens.has("reject-action:"+actionId));
  for(const body of [await html(ownerPage(overview,human,tokens,null,now)),await html(actionPage({frozen:false,action},human,actionId,tokens,now))]){
    assert.ok(!body.includes("Approve and allow sending once"));
    assert.ok(!body.includes("Approving needs a GitHub sign-in"));
    assert.ok(body.includes("<p>"+ownerResultText["account-changed"]+"</p>"));
    assert.ok(body.includes(">Reject</button>")&&body.includes('value="'+tokens.get("reject-action:"+actionId)+'"'));
    assert.ok(body.includes("<pre>Episode 12</pre>"),"the plan stays visible");
  }
  const live=base({agents:[agent({agentId})],externalActions:[{...action,accountChanged:false}]});
  const liveTokens=await ownerFormTokens(randomBytes(32).toString("base64url"),human.humanId,live,now,false);
  const liveBody=await html(actionPage({frozen:false,action:{...action,accountChanged:false}},human,actionId,liveTokens,now));
  assert.ok(liveBody.includes("Approve and allow sending once")&&liveBody.includes("Approving needs a GitHub sign-in")&&!liveBody.includes(ownerResultText["account-changed"]!));
});

test("ownerPage_ConnectedAccounts_ShowVerifiedChannelDisconnectAndConnectOnlyWhenConfigured",async()=>{
  const agentId=randomUUID();
  const overview=base({agents:[agent({agentId,credentials:[{credentialRef:"youtube",provider:"youtube",targetAccount:"UC"+"a".repeat(22),
    targetDisplay:"HSC <i>Beats</i>",revision:"rev-1",connectedAt:iso(now)}]})]});
  const key=randomBytes(32).toString("base64url");
  const configured=await ownerFormTokens(key,human.humanId,overview,now,true);
  assert.ok(configured.has("connect-youtube:"+agentId)&&configured.has("revoke-credential:"+agentId+":youtube"));
  const body=await html(ownerPage(overview,human,configured,null,now));
  assert.ok(body.includes("youtube as HSC &#60;i&#62;Beats&#60;/i&#62; (<code>UC"+"a".repeat(22)+"</code>); credential <code>youtube@rev-1</code>"));
  assert.ok(body.includes(">Disconnect</button>")&&body.includes(">Connect YouTube</button>"));
  const unconfigured=await ownerFormTokens(key,human.humanId,overview,now,false);
  assert.equal(unconfigured.has("connect-youtube:"+agentId),false);
  assert.ok(!(await html(ownerPage(overview,human,unconfigured,null,now))).includes("Connect YouTube"));
  assert.ok((await html(ownerPage(base({agents:[agent()]}),human,new Map(),"no-channel",now))).includes(ownerResultText["no-channel"]));
});

test("ownerPage_DisconnectWithWaitingActions_SaysTheyCanNeverBeApproved",async()=>{
  const agentId=randomUUID(),other=randomUUID();
  const credential=(credentialRef:string)=>({credentialRef,provider:"telegram",targetAccount:"-100"+credentialRef.length,targetDisplay:"Chan "+credentialRef,revision:"r1",connectedAt:iso(now)});
  const action=(over:Partial<Extract<OwnerOverview,{frozen:false}>["externalActions"][number]>)=>({actionId:randomUUID(),agentId,agentDisplayName:"a",
    operation:"publish_post",provider:"telegram",content:"x",credentialRef:"tg",credentialRevision:"r1",targetAccount:"-1002",targetDisplay:"Chan tg",
    planDigest:kid(),createdAt:iso(now),expiresAt:iso(now+1),media:[],accountChanged:false,...over});
  const agents=[agent({agentId,credentials:[credential("tg"),credential("tg2"),credential("one")]}),agent({agentId:other,credentials:[credential("tg")]})];
  const pending=[action({}),action({}),action({accountChanged:true}),action({credentialRef:"one"}),action({agentId:other,credentialRef:"tg2"})];
  const items=(body:string)=>body.split("<h2>Your agents</h2>")[1]!.split("<li>").filter(item=>item.includes("credential <code>"));
  const [tg,tg2,one,otherTg]=items(await html(ownerPage(base({agents,externalActions:pending}),human,new Map(),null,now)));
  // Two live actions on this agent's tg; the account-changed one and another agent's are not counted.
  assert.ok(tg!.includes("<p>2 actions waiting for your approval use this account. Disconnecting it means those actions can never be approved or sent.</p>"),tg);
  assert.ok(one!.includes("<p>1 action waiting for your approval uses this account. Disconnecting it means that action can never be approved or sent.</p>"),one);
  assert.ok(!tg2!.includes("waiting for your approval"),"another agent's action on the same reference is not this credential's");
  assert.ok(!otherTg!.includes("waiting for your approval"),"this agent's actions are not the other agent's");
  const cut=items(await html(ownerPage(base({agents,externalActions:pending,moreExternalActions:true}),human,new Map(),null,now)));
  assert.ok(cut[0]!.includes("<p>At least 2 actions waiting"),cut[0]);
  const none=items(await html(ownerPage(base({agents}),human,new Map(),null,now)));
  assert.ok(none.every(item=>!item.includes("waiting for your approval")));
});

test("ownerPage_ActionWithMedia_PreviewsOnlyItsFilesAndOffersPostizConnect",async()=>{
  const actionId=randomUUID(),agentId=randomUUID(),image=randomUUID(),video=randomUUID();
  const overview=base({agents:[agent({agentId})],connectors,externalActions:[{actionId,agentId,agentDisplayName:null,provider:"postiz",operation:"publish_post",
    content:JSON.stringify({text:"New beat",media:[{mediaId:image},{mediaId:video}]}),credentialRef:"postiz:abc",credentialRevision:"r",targetAccount:"tiktok/abc",
    targetDisplay:"tiktok · HSC Beats",planDigest:kid(),createdAt:iso(now),expiresAt:iso(now+1),media:[{mediaId:image,contentType:"image/png"},{mediaId:video,contentType:"video/mp4"}],accountChanged:false}]});
  const tokens=await ownerFormTokens(randomBytes(32).toString("base64url"),human.humanId,overview,now,false);
  const response=ownerPage(overview,human,tokens,null,now);
  assert.match(response.headers.get("content-security-policy")!,/img-src 'self'; media-src 'self'/);
  const body=await html(response);
  assert.ok(body.includes('<img alt="Attached file '+image+'" width="360" src="/account/media/'+actionId+'/'+image+'">'));
  assert.ok(body.includes('<video controls preload="metadata" width="360" src="/account/media/'+actionId+'/'+video+'"></video>'));
  for(const connector of connectors)
    assert.ok(body.includes('<form method="post" action="/account/connect"><input type="hidden" name="action" value="'+tokens.get("connect-account:"+agentId+":"+connector.provider)+'">'),connector.provider);
  assert.ok(body.includes('<input type="password" name="apiKey" autocomplete="off" required>'));
  assert.ok(body.includes('<input type="password" name="appPassword" autocomplete="off" required pattern="[a-z0-9]{4}(-[a-z0-9]{4}){3}">'));
});

test("ownerPage_ConnectForm_BuiltFromThePlatformsFieldsAndEscaped",async()=>{
  const agentId=randomUUID();
  const hostile:OwnerConnector={provider:"demo",label:'<b>Demo</b>',help:'Use "x" & <y>',
    fields:[{name:"user",label:"User <i>",secret:false,placeholder:'a"b'},{name:"key",label:"Key",secret:true,pattern:'[a-z]+"'}]};
  const overview=base({agents:[agent({agentId})],connectors:[hostile]});
  const tokens=await ownerFormTokens(randomBytes(32).toString("base64url"),human.humanId,overview,now,false);
  const body=await html(ownerPage(overview,human,tokens,null,now));
  assert.ok(body.includes('<label>User &#60;i&#62; <input type="text" name="user" autocomplete="off" required placeholder="a&#34;b"></label>'));
  assert.ok(body.includes('<input type="password" name="key" autocomplete="off" required pattern="[a-z]+&#34;">'),"a secret field is a password input");
  assert.ok(body.includes("Connect &#60;b&#62;Demo&#60;/b&#62;</button>")&&body.includes("<p>Use &#34;x&#34; &#38; &#60;y&#62;</p>"));
});

test("ownerPage_AgentPages_LinksToMoreAndFirstAgents",async()=>{
  const next=randomUUID();
  const first=await html(ownerPage(base({agents:[agent()],nextAgents:next}),human,new Map(),null,now));
  assert.ok(first.includes('<a href="/account?agents='+next+'">More agents</a>')&&!first.includes("First agents"));
  const middle=await html(ownerPage(base({agents:[agent()],nextAgents:next}),human,new Map(),null,now,randomUUID()));
  assert.ok(middle.includes('<a href="/account">First agents</a>')&&middle.includes("More agents"));
  const last=await html(ownerPage(base({agents:[agent()]}),human,new Map(),null,now,randomUUID()));
  assert.ok(last.includes("First agents")&&!last.includes("More agents"));
  const none=await html(ownerPage(base(),human,new Map(),null,now));
  assert.ok(none.includes("You do not own any agents yet.")&&!none.includes("<a "));
});

test("ownerPage_ActionOfAnAgentNotOnThisPage_NamedFromTheAction",async()=>{
  const actionId=randomUUID();
  const overview=base({agents:[agent()],externalActions:[{actionId,agentId:randomUUID(),agentDisplayName:"Late desk",provider:"test-http",operation:"publish_text",
    content:"x",credentialRef:"c",credentialRevision:"r",targetAccount:"t",targetDisplay:"T",planDigest:kid(),createdAt:iso(now),expiresAt:iso(now+1),media:[],accountChanged:false}]});
  assert.ok((await html(ownerPage(overview,human,new Map(),null,now))).includes("Late desk (self-reported, not verified) wants to publish_text"));
});

test("ownerPage_OwnerAppGrants_EscapedUnverifiedWithRevokeAndTokens",async()=>{
  const hostile=randomUUID(),unnamed=randomUUID(),revoked=randomUUID();
  const overview=base({ownerGrants:[
    {grantId:hostile,clientId:'https://office.example/"><script>x</script>',clientName:'<script>alert(1)</script>',createdAt:iso(now),
      webhooks:[{webhookId:randomUUID(),url:'https://office.example/hooks?a="><img src=x>',createdAt:iso(now)},{webhookId:randomUUID(),url:"https://office.example/second",createdAt:iso(now+1)}],appDecides:true},
    {grantId:unnamed,clientId:"https://client.example/cimd.json",clientName:null,createdAt:iso(now-1000),webhooks:[],appDecides:false}],revokedOwnerGrantIds:[revoked]});
  const tokens=await ownerFormTokens(randomBytes(32).toString("base64url"),human.humanId,overview,now,false);
  // Only the grant the owner let decide gets a form that stops it.
  assert.deepEqual([...tokens.keys()].sort(),["revoke-grant:"+hostile,"revoke-grant:"+unnamed,"stop-app-decisions:"+hostile].sort());
  const body=await html(ownerPage(overview,human,tokens,null,now));
  assert.equal(body.match(/It may approve or reject your agents' pending external actions for you\./g)?.length,1,body);
  assert.equal(body.match(/It cannot approve or reject your agents' external actions/g)?.length,1,body);
  assert.ok(body.includes(">Stop it approving</button>"),body);
  assert.ok(body.includes("<h2>Apps that manage all your agents</h2>"));
  assert.ok(body.includes("including agents you add later"));
  assert.ok(!body.includes("<script>"),"client-supplied text must not become markup");
  assert.ok(body.includes("&#60;script&#62;alert(1)&#60;/script&#62; (self-reported, not verified)"));
  assert.ok(body.includes("Unnamed client"));
  assert.ok(!body.includes("<img"),"app-supplied webhook URLs must not become markup");
  assert.ok(body.includes("Weave sends it the status of your agents' actions (never their content) at:</p><ul><li><code>https://office.example/hooks?a=&#34;&#62;&#60;img src=x&#62;</code>; registered "+iso(now)+"</li><li><code>https://office.example/second</code>"),body);
  assert.equal(body.match(/It receives no status updates from Weave\./g)?.length,1,"the grant without webhooks says so");
  assert.ok(!/<a [^>]*office\.example\/hooks/.test(body),"a webhook URL is never a link");
  assert.equal(body.match(/<form /g)!.length,tokens.size);
  assert.ok(!body.includes(revoked),"revoked owner grant IDs are for clean-up only");
  const none=await html(ownerPage(base(),human,new Map(),null,now));
  assert.ok(none.includes("No app manages all your agents."));
  // A mailbox deployed before the list (no `webhooks` member) shows the grant without any webhook sentence.
  const older=await html(ownerPage(base({ownerGrants:[{grantId:unnamed,clientId:"https://client.example/cimd.json",clientName:null,createdAt:iso(now)} as OwnerAppGrant]}),human,new Map(),null,now));
  assert.ok(older.includes("Unnamed client")&&!older.includes("status updates")&&!older.includes("Weave sends it"),older);
  assert.ok(!older.includes("approve or reject"),"a mailbox that predates the permission shows no permission sentence");
});

// A mailbox that predates `settled`, or a status the page has no text for, reads as the generic sentence, never as text the page
// would otherwise invent (an inherited property name included).
test("actionPage_SettledMissingPendingOrUnlisted_GenericSentence",async()=>{
  const actionId=randomUUID(),generic="This action is not waiting for your approval. It may already be decided or expired.";
  for(const settled of [undefined,null,{status:"pending",failure:null},{status:"toString",failure:null},{status:"later",failure:"x"}]){
    const body=await html(actionPage({frozen:false,action:null,settled},human,actionId,new Map(),now));
    assert.ok(body.includes("<p>"+generic+"</p>")&&!body.includes("undefined")&&!body.includes("function"),JSON.stringify(settled)+": "+body);
  }
  assert.ok((await html(actionPage({frozen:false,action:null,settled:{status:"completed",failure:null}},human,actionId,new Map(),now))).includes("This action was sent."));
});

// A sent action shows the platform's reference and its tracked publication, escaped; a link is offered only for an absolute https
// URL, and none of it appears for an action that was not sent.
test("actionPage_SentWithReceiptAndPublication_ReferenceStateAndHttpsLinkOnly",async()=>{
  const actionId=randomUUID();
  const page=async(settled:SettledActionView)=>html(actionPage({frozen:false,action:null,settled},human,actionId,new Map(),now));
  const plain=await page({status:"completed",failure:null,receipt:"posted:<b>1",publication:null});
  assert.ok(plain.includes("This action was sent.")&&plain.includes("<code>posted:&#60;b&#62;1</code>")&&!plain.includes("<b>1"),plain);
  assert.ok(!plain.includes("published")&&!plain.includes("<a href=\"https"),plain);
  const queued=await page({status:"completed",failure:null,receipt:"scheduled:1",publication:{state:"queued",url:null,error:null}});
  assert.ok(queued.includes("The platform has not reported it published yet. Nothing is sent again.")&&!queued.includes("checks again"),queued);
  const published=await page({status:"completed",failure:null,receipt:"posted:1",publication:{state:"published",url:"https://x.test/p?a=1&b=\"2",error:null}});
  assert.ok(published.includes('The platform published it. <a href="https://x.test/p?a=1&#38;b=&#34;2" rel="noopener noreferrer">View it on the platform</a>'),published);
  for(const url of ["javascript:alert(1)","http://x.test/p","//x.test/p","https://",null]){
    const body=await page({status:"completed",failure:null,receipt:"posted:1",publication:{state:"published",url,error:null}});
    assert.ok(body.includes("The platform published it.</p>")&&!body.includes("View it on the platform"),String(url)+": "+body);
  }
  const failed=await page({status:"completed",failure:null,receipt:"posted:1",publication:{state:"failed",url:null,error:"quota <exceeded>"}});
  assert.ok(failed.includes("The platform reported that publishing failed: quota &#60;exceeded&#62;. Weave will not send it again."),failed);
  // A post whose receipt names its own address links to it, https only and escaped.
  const linked=await page({status:"completed",failure:null,receipt:"1",publication:null,postUrl:"https://x.test/i/web/status/1?a=\"<b>"});
  assert.ok(linked.includes('<p><a href="https://x.test/i/web/status/1?a=&#34;&#60;b&#62;" rel="noopener noreferrer">View the post on the platform</a></p>'),linked);
  for(const postUrl of ["javascript:alert(1)","http://x.test/p","//x.test/p","https://",null,undefined]){
    const body=await page({status:"completed",failure:null,receipt:"1",publication:null,postUrl});
    assert.ok(!body.includes("View the post"),String(postUrl)+": "+body);
  }
  // Only a sent action shows these details, whatever the mailbox sends with another status; an older mailbox omits them.
  for(const status of ["failed","unknown","approved"]){
    const withLink=await page({status,failure:null,receipt:"1",publication:null,postUrl:"https://x.test/p"});
    assert.ok(!withLink.includes("View the post"),status+": "+withLink);
    const body=await page({status,failure:null,receipt:"posted:1",publication:{state:"published",url:"https://x.test/p",error:null}});
    assert.ok(!body.includes("posted:1")&&!body.includes("published it"),status+": "+body);
  }
  const older=await page({status:"completed",failure:null});
  assert.ok(older.includes("This action was sent.")&&!older.includes("reference"),older);
});

// The page of a settled action says which action it was: agent, operation, the account the plan bound and the content, escaped
// and verbatim; without the credential's name only the account ID; an older mailbox or a malformed description adds nothing.
test("actionPage_SettledWithPlan_SaysWhichActionItWasEscaped",async()=>{
  const actionId=randomUUID(),agentId=randomUUID();
  const page=async(settled:SettledActionView)=>html(actionPage({frozen:false,action:null,settled},human,actionId,new Map(),now));
  const about={agentId,agentDisplayName:"<b>Desk</b>",provider:"post<iz>",operation:"publish_post",targetAccount:"acct<1>",targetDisplay:"Shop & Co",content:"Line 1\n<script>x</script>"};
  for(const status of ["approved","rejected","expired","dispatching","completed","failed","unknown"]){
    const body=await page({status,failure:status==="failed"?"owner-changed":null,about});
    assert.ok(body.includes("<p>&#60;b&#62;Desk&#60;/b&#62; (self-reported, not verified) asked to publish_post on post&#60;iz&#62; as Shop &#38; Co (<code>acct&#60;1&#62;</code>).</p>"),status+": "+body);
    assert.ok(body.includes("<p>Content, exactly as proposed:</p><pre>Line 1\n&#60;script&#62;x&#60;/script&#62;</pre>")&&!body.includes("<script>")&&!body.includes("<b>Desk"),status+": "+body);
  }
  const unnamed=await page({status:"rejected",failure:null,about:{...about,agentDisplayName:null,targetDisplay:null}});
  assert.ok(unnamed.includes("<p>Unnamed agent "+agentId.slice(0,8)+" asked to publish_post on post&#60;iz&#62; as <code>acct&#60;1&#62;</code>.</p>"),unnamed);
  for(const odd of [undefined,null,{...about,content:7},{...about,provider:undefined},"text"] as unknown[]){
    const body=await page({status:"rejected",failure:null,about:odd as SettledActionView["about"]});
    assert.ok(body.includes("This action was rejected.")&&!body.includes("asked to")&&!body.includes("<pre>"),JSON.stringify(odd)+": "+body);
  }
  // An unlisted status reads as the generic sentence and never shows the plan.
  const generic=await page({status:"later",failure:null,about});
  assert.ok(generic.includes("not waiting for your approval")&&!generic.includes("asked to")&&!generic.includes("<pre>"),generic);
});

// Only an approval not yet used offers its withdrawal, and only with a signed token posting back to the action's own page.
test("actionPage_SettledApprovedWithToken_OffersWithdrawalOnlyThen",async()=>{
  const actionId=randomUUID(),tokens=new Map([["reject-action:"+actionId,"signed<token>"]]);
  const page=async(status:string,map:ReadonlyMap<string,string>=tokens)=>html(actionPage({frozen:false,action:null,settled:{status,failure:null}},human,actionId,map,now));
  const approved=await page("approved");
  assert.ok(approved.includes('<form method="post" action="/account/actions/'+actionId+'"><input type="hidden" name="action" value="signed&#60;token&#62;"><button type="submit">Withdraw approval</button></form>'),approved);
  assert.ok(approved.includes("Withdraw the approval and it will never be sent."),approved);
  for(const status of ["rejected","expired","dispatching","completed","failed","unknown","later"]){
    const body=await page(status);
    assert.ok(!body.includes("Withdraw")&&!body.includes("<form"),status+": "+body);
  }
  const unsigned=await page("approved",new Map());
  assert.ok(!unsigned.includes("<form")&&!unsigned.includes("Withdraw"),"no token, no form and no offer: "+unsigned);
});

// An approval the agent has not used says by when it must be sent, read from the mailbox and escaped; nothing else says it.
test("actionPage_SettledApprovedWithSendBy_SaysWhenItExpires",async()=>{
  const actionId=randomUUID(),deadline="2026-11-19T06:00:00Z";
  const page=async(settled:SettledActionView)=>html(actionPage({frozen:false,action:null,settled},human,actionId,new Map(),now));
  const approved=await page({status:"approved",failure:null,sendBy:deadline});
  assert.ok(approved.includes("<p>The agent must send it by "+deadline+". After that it expires and is never sent.</p>"),approved);
  assert.ok((await page({status:"approved",failure:null,sendBy:"<b>soon</b>"})).includes("send it by &#60;b&#62;soon&#60;/b&#62;."),"escaped");
  for(const status of ["rejected","expired","dispatching","completed","failed","unknown","later"]){
    const body=await page({status,failure:null,sendBy:deadline});
    assert.ok(!body.includes("must send it by")&&!body.includes(deadline),status+": "+body);
  }
  // A mailbox that predates `sendBy`, or a malformed one, adds nothing.
  for(const sendBy of [undefined,null,"",42 as unknown as string]){
    const body=await page({status:"approved",failure:null,sendBy});
    assert.ok(body.includes("You approved this action.")&&!body.includes("must send it by"),String(sendBy)+": "+body);
  }
});

// The owner page lists what became of the owner's recent actions, each linking to its own page, with every stored value escaped.
test("ownerPage_RecentActions_EachOutcomeLinkedAndEscaped",async()=>{
  const agentId=randomUUID();
  const statuses=["approved","rejected","expired","dispatching","completed","failed","unknown"] as const;
  const recentActions=statuses.map((status,i)=>({actionId:randomUUID(),agentId,agentDisplayName:i===0?"<b>Desk</b>":null,provider:"post<iz>",operation:"publish_post",
    status,failure:status==="failed"?"provider-refused<script>":null,changedAt:iso(now-i*1000)}));
  const html=await ownerPage(base({recentActions}),human,new Map(),null,now).text();
  assert.match(html,/<h2>Recent actions<\/h2>/);
  for(const action of recentActions)assert.ok(html.includes('<a href="/account/actions/'+action.actionId+'">'),action.status);
  assert.ok(!html.includes("<b>Desk</b>")&&html.includes("&#60;b&#62;Desk&#60;/b&#62;"),"the self-reported name is escaped");
  assert.ok(!html.includes("post<iz>")&&!html.includes("<script>"),"stored provider and failure text are escaped");
  assert.match(html,/: approved; nothing is sent until the agent sends it, once;/);
  assert.match(html,/: rejected; nothing will be sent;/);
  assert.match(html,/: expired without being sent;/);
  assert.match(html,/: being sent; it will not be sent again;/);
  assert.match(html,/: sent; /);
  assert.match(html,/: not sent \(<code>provider-refused&#60;script&#62;<\/code>\);/);
  assert.match(html,/: sent, but whether the platform accepted it is unknown; it will not be sent again;/);
  // Newest first, as the mailbox lists them.
  assert.ok(html.indexOf(recentActions[0]!.actionId)<html.indexOf(recentActions[6]!.actionId));
  // A failure code is shown only for a failed action.
  const quiet=await ownerPage(base({recentActions:[{...recentActions[4]!,failure:"leftover"}]}),human,new Map(),null,now).text();
  assert.ok(!quiet.includes("leftover"));
});

test("ownerPage_RecentActions_NoneOrUnlistedStatus_NeverGuessed",async()=>{
  const none=await ownerPage(base(),human,new Map(),null,now).text();
  assert.match(none,/None of your agents' actions was decided, sent or expired in the last 7 days\./);
  // A mailbox that predates the list omits it.
  const older={...base()} as Record<string,unknown>;delete older.recentActions;
  assert.match(await ownerPage(older as OwnerOverview,human,new Map(),null,now).text(),/None of your agents' actions was decided/);
  const odd=await ownerPage(base({recentActions:[{actionId:randomUUID(),agentId:randomUUID(),agentDisplayName:null,provider:"x",operation:"publish_post",
    status:"pending" as never,failure:"leftover",changedAt:iso(now)}]}),human,new Map(),null,now).text();
  assert.match(odd,/: status not shown here; /);
  assert.ok(!odd.includes("leftover"));
});

// When the mailbox left some waiting actions out, the page says so; when it left recent ones out, it links to the older ones after
// the last listed. Otherwise, or from a mailbox that predates the flags, it adds neither.
test("ownerPage_ListsLeftSomeOut_SaysHowManyWaitingAreShownAndLinksToOlderRecent",async()=>{
  const agentId=randomUUID();
  const recentActions=[0,1].map(i=>({actionId:randomUUID(),agentId,agentDisplayName:null,provider:"x",operation:"publish_post",status:"rejected" as const,failure:null,changedAt:iso(now-i)}));
  const olderLink='<p><a href="/account?recent='+recentActions[1]!.actionId+'">Older actions</a></p>';
  const both=await ownerPage(base({recentActions,moreExternalActions:true,moreRecentActions:true}),human,new Map(),null,now).text();
  assert.match(both,/<p>Only the 0 oldest actions waiting for approval are shown\. Approve or reject some to see the rest here; each one's own page, where the app that proposed it links you, also works\.<\/p>/);
  assert.ok(both.includes(olderLink),both);
  assert.ok(both.indexOf("Only the 0 oldest")<both.indexOf("<h2>Recent actions</h2>")&&both.indexOf(olderLink)>both.indexOf("<h2>Recent actions</h2>"),"each under its own list");
  assert.ok(!both.includes("Newest actions"),"the first page links to no newer one");
  const pendingOnly=await ownerPage(base({recentActions,moreExternalActions:true}),human,new Map(),null,now).text();
  assert.ok(pendingOnly.includes("oldest actions waiting")&&!pendingOnly.includes("Older actions"));
  const recentOnly=await ownerPage(base({recentActions,moreRecentActions:true}),human,new Map(),null,now).text();
  assert.ok(!recentOnly.includes("oldest actions waiting")&&recentOnly.includes(olderLink));
  const complete=await ownerPage(base({recentActions}),human,new Map(),null,now).text();
  assert.ok(!complete.includes("Only the ")&&!complete.includes("Older actions"),"nothing left out, no note or link");
  const older={...base({recentActions})} as Record<string,unknown>;delete older.moreExternalActions;delete older.moreRecentActions;
  const unflagged=await ownerPage(older as OwnerOverview,human,new Map(),null,now).text();
  assert.ok(!unflagged.includes("Only the ")&&!unflagged.includes("Older actions"),"a mailbox without the flags adds neither");
});

// A later page of recent actions links back to the newest, and on only while more are left out; an empty later page says there are
// no older ones instead of claiming none happened in the last 7 days.
test("ownerPage_LaterRecentPage_LinksToNewestAndSaysWhenNoneAreOlder",async()=>{
  const agentId=randomUUID(),cursor=randomUUID();
  const recentActions=[0,1].map(i=>({actionId:randomUUID(),agentId,agentDisplayName:null,provider:"x",operation:"publish_post",status:"rejected" as const,failure:null,changedAt:iso(now-i)}));
  const middle=await ownerPage(base({recentActions,moreRecentActions:true}),human,new Map(),null,now,null,cursor).text();
  assert.ok(middle.includes('<p><a href="/account">Newest actions</a> · <a href="/account?recent='+recentActions[1]!.actionId+'">Older actions</a></p>'),middle);
  const last=await ownerPage(base({recentActions}),human,new Map(),null,now,null,cursor).text();
  assert.ok(last.includes('<p><a href="/account">Newest actions</a></p>')&&!last.includes("Older actions"),last);
  const empty=await ownerPage(base(),human,new Map(),null,now,null,cursor).text();
  assert.ok(empty.includes('<p>No older actions from the last 7 days.</p><p><a href="/account">Newest actions</a></p>'),empty);
  assert.ok(!empty.includes("None of your agents' actions was decided"));
  assert.equal(empty.split("Newest actions").length-1,1,"one link back, not two");
});

// A not-sent action explains its stored failure code in plain words and says what the owner can do; a code the page does not
// know, or an inherited property name, adds nothing, and no explanation appears for an action with another status.
test("actionPage_FailedWithKnownCode_ExplainsWhatHappenedAndWhatToDo",async()=>{
  const actionId=randomUUID();
  const page=async(settled:SettledActionView)=>html(actionPage({frozen:false,action:null,settled},human,actionId,new Map(),now));
  const expected:Record<string,string>={
    "owner-changed":"The agent's owner changed after the approval, so Weave did not send it.",
    "owner-frozen":"The owner's GitHub identity was frozen before it could be sent, so Weave did not send it.",
    "credential-changed":"The account it posts through was reconnected or disconnected after the approval, so Weave did not send it. Your agent can propose it again.",
    "credential-unreadable":"Weave could not open the stored sign-in for that account, so it did not send it. Disconnect the account, connect it again, then have your agent propose it again.",
    "plan-changed":"Weave's connector for this platform changed after the approval, so the approved plan no longer applies and Weave did not send it. Your agent can propose it again.",
    "provider-rejected: credential-refused":"The platform refused the connected account's sign-in, so nothing was posted. Disconnect the account, connect it again, then have your agent propose it again.",
    "provider-rejected: media-unavailable":"A file in it was no longer stored as approved, so nothing was posted. Your agent can upload it and propose it again.",
    "provider-rejected: x-refused-403":"The platform refused it, so nothing was posted.",
    "provider-rejected: toString":"The platform refused it, so nothing was posted."
  };
  for(const [code,text] of Object.entries(expected)){
    const body=await page({status:"failed",failure:code});
    assert.ok(body.includes("This action was not sent. Reason: "+code+". "+text+"</p>"),code+": "+body);
  }
  for(const code of ["later-code","toString","provider-rejected","provider-rejected: "]){
    const body=await page({status:"failed",failure:code});
    assert.ok(body.includes("This action was not sent. Reason: "+code+".</p>"),code+": "+body);
  }
  assert.ok((await page({status:"failed",failure:null})).includes("This action was not sent.</p>"));
  for(const status of ["unknown","completed","rejected"]){
    const body=await page({status,failure:"credential-unreadable"});
    assert.ok(!body.includes("could not open")&&!body.includes("Reason:"),status+": "+body);
  }
});
