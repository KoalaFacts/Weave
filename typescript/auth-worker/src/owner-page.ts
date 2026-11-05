import type {OwnerAddress,OwnerBindingRequest,OwnerGrant,OwnerKey,OwnerOverview} from "../../mailbox-worker/src/owner-actions.ts";
import type {OwnerExternalAction} from "../../mailbox-worker/src/external-actions.ts";
import type {OwnerCredential} from "../../mailbox-worker/src/external-credentials.ts";
import type {OwnerConnector} from "../../mailbox-worker/src/publishing.ts";
import {freshAuthentication,type HumanSession} from "./human-session.ts";
import {ownerActionToken} from "./owner-forms.ts";

const esc=(s:string)=>s.replace(/[&<>"']/g,c=>"&#"+c.charCodeAt(0)+";");
const headers={
  "Content-Type":"text/html; charset=utf-8","Cache-Control":"no-store","Pragma":"no-cache","Referrer-Policy":"no-referrer",
  "X-Content-Type-Options":"nosniff","X-Frame-Options":"DENY",
  // Images and video load only from this origin: the owner's own approval previews.
  "Content-Security-Policy":"default-src 'none'; img-src 'self'; media-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'"
};
const shell=(title:string,body:string,status=200)=>new Response(
  '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>'+
  esc(title)+"</title></head><body><main>"+body+"</main></body></html>",{status,headers});

/** Text for `?result=`: the success, each failure reason the mailbox worker can return and an expired form. Anything else is ignored. */
export const ownerResultText:Readonly<Record<string,string>>=Object.freeze({
  done:"Done.",
  "stale-authentication":"Sign in with GitHub again, then retry: this needs a sign-in from the last 10 minutes.",
  frozen:"This GitHub identity is frozen; contact the operator.",
  "not-found":"That request, key or connected client was not found.",
  "not-pending":"That request was already decided or cancelled.",
  expired:"That request has expired.",
  "agent-limit":"You already own the maximum of 10 agents.",
  "already-owned":"That agent already belongs to a person.",
  "key-in-use":"That key is already in use.",
  "key-limit":"The agent already has the maximum number of live keys.",
  "endorser-revoked":"The key that endorsed this request has been revoked.",
  "requesting-key-revoked":"The key that made this request has been revoked.",
  "keys-changed":"The agent's keys changed after you opened this page. Review the current keys and confirm again.",
  "plan-changed":"That action or its account changed after you opened this page. Review the current action and approve again.",
  connected:"Connected. Actions your agent proposes through this account still need your approval.",
  "connect-cancelled":"Connecting was cancelled; nothing was stored.",
  "provider-refused":"The provider did not complete the connection; nothing was stored. Try again.",
  "provider-unavailable":"Connecting this provider is not available on this server.",
  "missing-scope":"Grant every permission the provider asks for, or the agent cannot post; nothing was stored.",
  "no-channel":"No channel was found to connect; nothing was stored. For YouTube, pick the account or brand channel to post as; for Postiz, connect a channel in Postiz first.",
  "account-connected":"Connected. Each connected account is listed below; posts your agent proposes through them still need your approval.",
  "form-expired":"This page expired. Reload it and try again.",
  "cleanup-pending":"Access is revoked; token clean-up will be retried on your next visit.",
  "cleanup-complete":"Access is revoked and token clean-up is complete.",
  unavailable:"Weave could not complete that right now. Try again later."
});

/** The 43-character key ID in full, its first eight characters emphasised for comparison with what the agent shows. */
const fingerprint=(kid:string)=>"<code><strong>"+esc(kid.slice(0,8))+"</strong>"+esc(kid.slice(8))+"</code>";
const unverified=" (self-reported, not verified)";
const agentName=(agentId:string,displayName:string|null)=>
  displayName===null?"Unnamed agent "+esc(agentId.slice(0,8)):esc(displayName)+unverified;
const form=(label:string,key:string,tokens:ReadonlyMap<string,string>,target="/account")=>{
  const token=tokens.get(key);
  return token===undefined?"":'<form method="post" action="'+esc(target)+'"><input type="hidden" name="action" value="'+esc(token)+
    '"><button type="submit">'+esc(label)+"</button></form>";
};
const cooling=(key:OwnerKey,now:number)=>key.coolingUntil!==null&&Date.parse(key.coolingUntil)>now?" - still in its 24 h cooling window":"";

function bindingSection(requests:OwnerBindingRequest[],tokens:ReadonlyMap<string,string>,now:number):string{
  if(requests.length===0)return "<p>No agent is asking to be bound to you.</p>";
  return requests.map(request=>[
    "<section><h3>"+agentName(request.agentId,request.displayName)+"</h3>",
    "<p>Agent ID <code>"+esc(request.agentId)+"</code></p>",
    "<p>Requesting key "+fingerprint(request.requestingKid)+"</p>",
    "<p>Live keys on this agent:</p><ul>"+(request.liveKeys.map(key=>
      "<li>"+fingerprint(key.kid)+esc(cooling(key,now))+"</li>").join("")||"<li>none</li>")+"</ul>",
    "<p>Requested "+esc(request.createdAt)+"; expires "+esc(request.expiresAt)+"</p>",
    form("Confirm binding","confirm-binding:"+request.requestId,tokens),
    form("Reject","reject-binding:"+request.requestId,tokens),"</section>"
  ].join("")).join("");
}

/**
 * Each pending action shows exactly what the approval binds: the full content, the provider-verified account it acts as and the
 * credential revision. The content is agent-supplied text, escaped and shown verbatim.
 */
function actionsSection(actions:OwnerExternalAction[],agents:Extract<OwnerOverview,{frozen:false}>["agents"],tokens:ReadonlyMap<string,string>,
  target="/account"):string{
  if(actions.length===0)return "<p>No agent is waiting for an action to be approved.</p>";
  return actions.map(action=>{
    const agent=agents.find(candidate=>candidate.agentId===action.agentId);
    return ["<section><h3>"+agentName(action.agentId,agent?.displayName??null)+" wants to "+esc(action.operation)+" on "+esc(action.provider)+"</h3>",
      "<p>As "+esc(action.targetDisplay)+" (<code>"+esc(action.targetAccount)+"</code>), verified by "+esc(action.provider)+
        "; credential <code>"+esc(action.credentialRef)+"@"+esc(action.credentialRevision)+"</code></p>",
      "<p>Content, exactly as it will be sent:</p><pre>"+esc(action.content)+"</pre>",
      action.media.length===0?"":"<p>Files in this action:</p>"+action.media.map(file=>{
        const src="/account/media/"+esc(action.actionId)+"/"+esc(file.mediaId);
        return file.contentType.startsWith("video/")?'<video controls preload="metadata" width="360" src="'+src+'"></video>':'<img alt="Attached file '+esc(file.mediaId)+'" width="360" src="'+src+'">';
      }).join(""),
      "<p>Requested "+esc(action.createdAt)+"; approval and sending must finish by "+esc(action.expiresAt)+"</p>",
      form("Approve and allow sending once","confirm-action:"+action.actionId,tokens,target),
      form("Reject","reject-action:"+action.actionId,tokens,target),"</section>"].join("");
  }).join("");
}

/** Client names and IDs are self-reported by the client: escaped text, never a link. */
function grantsSection(grants:OwnerGrant[],tokens:ReadonlyMap<string,string>):string{
  if(grants.length===0)return "<h4>Connected clients</h4><p>No connected clients.</p>";
  return "<h4>Connected clients</h4><ul>"+grants.map(grant=>"<li>"+
    (grant.clientName===null?"Unnamed client":esc(grant.clientName)+unverified)+"; client ID <code>"+esc(grant.clientId)+"</code>; connected "+
    esc(grant.createdAt)+form("Revoke access","revoke-grant:"+grant.grantId,tokens)+"</li>").join("")+"</ul>";
}

/** The account each credential acts as is what the provider verified at connect time. Connect forms come from the platforms' own field lists. */
function credentialsSection(agentId:string,credentials:OwnerCredential[],connectors:OwnerConnector[],tokens:ReadonlyMap<string,string>):string{
  const connect=form("Connect YouTube","connect-youtube:"+agentId,tokens);
  const list=credentials.length===0?"<p>No connected accounts.</p>":"<ul>"+credentials.map(credential=>"<li>"+esc(credential.provider)+" as "+
    esc(credential.targetDisplay)+" (<code>"+esc(credential.targetAccount)+"</code>); credential <code>"+esc(credential.credentialRef)+"@"+
    esc(credential.revision)+"</code>; connected "+esc(credential.connectedAt)+
    form("Disconnect","revoke-credential:"+agentId+":"+credential.credentialRef,tokens)+"</li>").join("")+"</ul>";
  const forms=connectors.map(connector=>{
    const token=tokens.get("connect-account:"+agentId+":"+connector.provider);
    if(token===undefined)return "";
    const inputs=connector.fields.map(field=>"<label>"+esc(field.label)+' <input type="'+(field.secret?"password":"text")+'" name="'+esc(field.name)+
      '" autocomplete="off" required'+(field.pattern?' pattern="'+esc(field.pattern)+'"':"")+(field.placeholder?' placeholder="'+esc(field.placeholder)+'"':"")+"></label>").join("");
    return '<form method="post" action="/account/connect"><input type="hidden" name="action" value="'+esc(token)+'">'+inputs+
      '<button type="submit">Connect '+esc(connector.label)+"</button></form>"+(connector.help?"<p>"+esc(connector.help)+"</p>":"");
  }).join("");
  return "<h4>Connected accounts</h4>"+list+connect+forms;
}

function addressesSection(addresses:OwnerAddress[]):string{
  if(addresses.length===0)return "<h4>Addresses</h4><p>No addresses.</p>";
  return "<h4>Addresses</h4><ul>"+addresses.map(address=>"<li><code>"+esc(address.addressId)+"</code>"+
    (address.label===null?"":" label "+esc(address.label)+unverified)+"; "+(address.isPublic?"public":"private")+"; "+
    (address.hasAgentCard?"has an agent card":"no agent card")+"; created "+esc(address.createdAt)+"</li>").join("")+"</ul>";
}

function agentSection(overview:Extract<OwnerOverview,{frozen:false}>,tokens:ReadonlyMap<string,string>):string{
  if(overview.agents.length===0)return "<p>You do not own any agents yet.</p>";
  return overview.agents.map(agent=>[
    "<section><h3>"+agentName(agent.agentId,agent.displayName)+"</h3>",
    "<p>Agent ID <code>"+esc(agent.agentId)+"</code></p>",
    "<h4>Keys</h4><ul>"+(agent.keys.map(key=>"<li>"+fingerprint(key.kid)+
      (key.label===null?"":" label "+esc(key.label)+unverified)+"; created "+esc(key.createdAt)+"; last token issued "+
      esc(key.lastUsedAt??"never")+form("Revoke","revoke-key:"+agent.agentId+":"+key.kid,tokens)+"</li>").join("")||"<li>none</li>")+"</ul>",
    "<h4>Pending key requests</h4>"+(agent.keyRequests.length===0?"<p>None.</p>":"<ul>"+agent.keyRequests.map(request=>"<li>"+fingerprint(request.kid)+
      (request.label===null?"":" label "+esc(request.label)+unverified)+"; "+
      "endorsed by "+fingerprint(request.endorsedBy)+
      "; expires "+esc(request.expiresAt)+form("Confirm","confirm-key:"+request.requestId,tokens)+
      form("Reject","reject-key:"+request.requestId,tokens)+"</li>").join("")+"</ul>"),
    grantsSection(agent.grants,tokens),addressesSection(agent.addresses),credentialsSection(agent.agentId,agent.credentials,overview.connectors,tokens),
    "<h4>Lost every key?</h4><p>A recovery code lets the agent add a new key without any existing key. It replaces any earlier code.</p>",
    form("Issue recovery code","issue-recovery-code:"+agent.agentId,tokens),"</section>"
  ].join("")).join("");
}

/** The owner's page. `tokens` maps each form to its signed action token; a form without a token is not rendered. */
export function ownerPage(overview:OwnerOverview,human:HumanSession,tokens:ReadonlyMap<string,string>,result:string|null,now:number):Response{
  const shown=result!==null&&Object.hasOwn(ownerResultText,result)?ownerResultText[result]:null;
  const head=(shown===null?"":"<p role=\"status\">"+esc(shown)+"</p>")+"<h1>Your Weave agents</h1><p>Signed in as @"+esc(human.login)+"</p>";
  if(overview.frozen)return shell("Your Weave agents",head+"<p>This GitHub identity is frozen; contact the operator. Nothing can be changed from here.</p>");
  const fresh=freshAuthentication(human,now);
  return shell("Your Weave agents",[head,
    "<p>Confirming, approving an action, connecting an account, revoking a key or issuing a recovery code needs a GitHub sign-in in the last 10 minutes. "+(fresh?"Your current sign-in qualifies.":
      "Your current sign-in is older, so you will be asked to sign in again first.")+" Revoking a connected client's access or disconnecting an account does not.</p>",
    "<h2>Binding requests</h2>",bindingSection(overview.bindingRequests,tokens,now),
    "<h2>Actions waiting for approval</h2>",actionsSection(overview.externalActions,overview.agents,tokens),
    "<h2>Your agents</h2>",agentSection(overview,tokens)].join(""));
}

/** The address of the page for one action: what an app such as Envoylet links to so its owner can decide there. */
export const actionPagePath=(actionId:string)=>"/account/actions/"+actionId;
/** The outcome of a decision on the page for one action; failure reasons share the owner page's text. */
const actionResultText:Readonly<Record<string,string>>=Object.freeze({
  approved:"Approved. The agent may now send it once. You can close this page.",
  rejected:"Rejected. Nothing will be sent. You can close this page."
});

/**
 * The response to a decision posted on the page for one action. The outcome is what this request just did, never read back
 * from a URL, so a link cannot make the page claim a decision that was not made.
 */
export function actionDecisionPage(human:HumanSession,actionId:string,result:string):Response{
  const text=Object.hasOwn(actionResultText,result)?actionResultText[result]:Object.hasOwn(ownerResultText,result)?ownerResultText[result]:ownerResultText.unavailable;
  return shell("Approve an action",'<p role="status">'+esc(text)+"</p><h1>Approve an action</h1><p>Signed in as @"+esc(human.login)+"</p>"+
    '<p><a href="'+esc(actionPagePath(actionId))+'">Back to this action</a> · <a href="/account">All your agents and actions</a></p>');
}

/**
 * The page for one pending action, so an app can send its owner here to decide: the same exact plan, files and forms as the
 * owner page, posting back to this address. An action that is not pending for this human is described the same way whether it
 * was decided, expired or belongs to someone else.
 */
export function actionPage(overview:OwnerOverview,human:HumanSession,actionId:string,tokens:ReadonlyMap<string,string>,now:number):Response{
  const head="<h1>Approve an action</h1><p>Signed in as @"+esc(human.login)+"</p>";
  if(overview.frozen)return shell("Approve an action",head+"<p>This GitHub identity is frozen; contact the operator. Nothing can be changed from here.</p>");
  const action=overview.externalActions.find(candidate=>candidate.actionId===actionId);
  const all='<p><a href="/account">All your agents and actions</a></p>';
  if(!action)return shell("Approve an action",head+"<p>This action is not waiting for your approval. It may already be decided or expired.</p>"+all);
  const fresh=freshAuthentication(human,now);
  return shell("Approve an action",[head,
    "<p>Approving needs a GitHub sign-in in the last 10 minutes. "+(fresh?"Your current sign-in qualifies.":"You will be asked to sign in again first.")+"</p>",
    actionsSection([action],overview.agents,tokens,actionPagePath(actionId)),all].join(""));
}

/** The one-time recovery code, shown in the response body only: never in a URL, redirect or log. */
export function recoveryCodePage(agentId:string,code:string,expiresAt:string):Response{
  return shell("Recovery code",[
    "<h1>Recovery code</h1>",
    "<p>This code is shown once. Copy it now; it is not stored and cannot be shown again.</p>",
    "<p><code>"+esc(code)+"</code></p>",
    "<p>It is single-use and expires at "+esc(expiresAt)+". Issuing another code for the same agent replaces it.</p>",
    "<p>The agent redeems it with <code>POST /v1/key-requests</code> on the Weave mailbox, sending agentId <code>"+esc(agentId)+
    "</code>, this code, and a DPoP proof made with the new key it wants to add.</p>",
    '<p><a href="/account">Back to your agents</a></p>'].join(""));
}

/** One signed token per form the page can render, keyed as `ownerPage` looks them up. */
/** `youtube` is whether this server can connect YouTube; without it no connect form is offered. */
export async function ownerFormTokens(key:string,humanId:string,overview:OwnerOverview,now:number,youtube:boolean):Promise<Map<string,string>>{
  const tokens=new Map<string,string>();
  if(overview.frozen)return tokens;
  const add=async(name:string,action:Parameters<typeof ownerActionToken>[2])=>{tokens.set(name,await ownerActionToken(key,humanId,action,now));};
  for(const request of overview.bindingRequests){
    // The confirmation is bound to the live keys shown next to it; the mailbox worker refuses it if they changed.
    await add("confirm-binding:"+request.requestId,{kind:"confirm-binding",requestId:request.requestId,keysDigest:request.keysDigest});
    await add("reject-binding:"+request.requestId,{kind:"reject-binding",requestId:request.requestId});
  }
  for(const action of overview.externalActions){
    // The approval is bound to the plan shown above it; the mailbox worker refuses it if anything in that plan changed.
    await add("confirm-action:"+action.actionId,{kind:"confirm-action",requestId:action.actionId,planDigest:action.planDigest});
    await add("reject-action:"+action.actionId,{kind:"reject-action",requestId:action.actionId});
  }
  for(const agent of overview.agents){
    await add("issue-recovery-code:"+agent.agentId,{kind:"issue-recovery-code",agentId:agent.agentId});
    for(const k of agent.keys)await add("revoke-key:"+agent.agentId+":"+k.kid,{kind:"revoke-key",agentId:agent.agentId,kid:k.kid});
    for(const r of agent.keyRequests){
      await add("confirm-key:"+r.requestId,{kind:"confirm-key",requestId:r.requestId});
      await add("reject-key:"+r.requestId,{kind:"reject-key",requestId:r.requestId});
    }
    for(const g of agent.grants)await add("revoke-grant:"+g.grantId,{kind:"revoke-grant",grantId:g.grantId});
    for(const c of agent.credentials)
      await add("revoke-credential:"+agent.agentId+":"+c.credentialRef,{kind:"revoke-credential",agentId:agent.agentId,credentialRef:c.credentialRef});
    if(youtube)await add("connect-youtube:"+agent.agentId,{kind:"connect-youtube",agentId:agent.agentId});
    for(const connector of overview.connectors)
      await add("connect-account:"+agent.agentId+":"+connector.provider,{kind:"connect-account",agentId:agent.agentId,provider:connector.provider});
  }
  return tokens;
}
