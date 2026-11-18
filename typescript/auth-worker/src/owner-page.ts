import type {OwnerAddress,OwnerAppGrant,OwnerBindingRequest,OwnerGrant,OwnerKey,OwnerOverview} from "../../mailbox-worker/src/owner-actions.ts";
import type {OwnerExternalAction,OwnerRecentAction} from "../../mailbox-worker/src/external-actions.ts";
import type {OwnerCredential} from "../../mailbox-worker/src/external-credentials.ts";
import type {OwnerConnector} from "../../mailbox-worker/src/publishing.ts";
import {freshAuthentication,type HumanSession} from "./human-session.ts";
import {ownerActionToken} from "./owner-forms.ts";

const esc=(s:string)=>s.replace(/[&<>"']/g,c=>"&#"+c.charCodeAt(0)+";");
const headers={
  // Native form POSTs need a non-null Origin for the auth worker's same-origin check.
  "Content-Type":"text/html; charset=utf-8","Cache-Control":"no-store","Pragma":"no-cache","Referrer-Policy":"same-origin",
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
  "agent-limit":"You already own as many agents as this server allows.",
  "already-owned":"That agent already belongs to a person.",
  "key-in-use":"That key is already in use.",
  "key-limit":"The agent already has the maximum number of live keys.",
  "endorser-revoked":"The key that endorsed this request has been revoked.",
  "requesting-key-revoked":"The key that made this request has been revoked.",
  "keys-changed":"The agent's keys changed after you opened this page. Review the current keys and confirm again.",
  "plan-changed":"That action changed after you opened this page. Review the current action and approve again.",
  "account-changed":"The account this action posts through was reconnected or disconnected after the action was proposed, so it can no longer be approved. Reject it; your agent can propose it again.",
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
function actionsSection(actions:OwnerExternalAction[],tokens:ReadonlyMap<string,string>,target="/account"):string{
  if(actions.length===0)return "<p>No agent is waiting for an action to be approved.</p>";
  return actions.map(action=>{
    return ["<section><h3>"+agentName(action.agentId,action.agentDisplayName)+" wants to "+esc(action.operation)+" on "+esc(action.provider)+"</h3>",
      "<p>As "+esc(action.targetDisplay)+" (<code>"+esc(action.targetAccount)+"</code>), verified by "+esc(action.provider)+
        "; credential <code>"+esc(action.credentialRef)+"@"+esc(action.credentialRevision)+"</code></p>",
      "<p>Content, exactly as it will be sent:</p><pre>"+esc(action.content)+"</pre>",
      action.media.length===0?"":"<p>Files in this action:</p>"+action.media.map(file=>{
        const src="/account/media/"+esc(action.actionId)+"/"+esc(file.mediaId);
        return file.contentType.startsWith("video/")?'<video controls preload="metadata" width="360" src="'+src+'"></video>':'<img alt="Attached file '+esc(file.mediaId)+'" width="360" src="'+src+'">';
      }).join(""),
      "<p>Requested "+esc(action.createdAt)+"; approval and sending must finish by "+esc(action.expiresAt)+"</p>",
      // A changed account can never be approved (the confirm answers account-changed), so only rejecting is offered.
      action.accountChanged?"<p>"+esc(ownerResultText["account-changed"]!)+"</p>":form("Approve and allow sending once","confirm-action:"+action.actionId,tokens,target),
      form("Reject","reject-action:"+action.actionId,tokens,target),"</section>"].join("");
  }).join("");
}

/** What the owner page's recent list says each outcome is; an unlisted status is shown as unknown to this page, never guessed. */
const recentText:Readonly<Record<string,string>>=Object.freeze({
  approved:"approved; nothing is sent until the agent sends it, once",
  rejected:"rejected; nothing will be sent",
  expired:"expired without being sent",
  dispatching:"being sent; it will not be sent again",
  completed:"sent",
  failed:"not sent",
  unknown:"sent, but whether the platform accepted it is unknown; it will not be sent again"
});

/**
 * The owner's agents' actions that stopped waiting in the last 7 days, newest first, each linking to its own page, which says
 * the rest (the platform's reference, the publication). A mailbox that predates the list omits it, which reads as none.
 */
function recentSection(actions:readonly OwnerRecentAction[]|undefined):string{
  if(!actions||actions.length===0)return "<p>None of your agents' actions was decided, sent or expired in the last 7 days.</p>";
  return "<ul>"+actions.map(action=>{
    const outcome=Object.hasOwn(recentText,action.status)?recentText[action.status]!:"status not shown here";
    const failure=action.status==="failed"&&action.failure?" (<code>"+esc(action.failure)+"</code>)":"";
    return '<li><a href="'+esc(actionPagePath(encodeURIComponent(action.actionId)))+'">'+agentName(action.agentId,action.agentDisplayName)+": "+
      esc(action.operation)+" on "+esc(action.provider)+"</a>: "+esc(outcome)+failure+"; "+esc(action.changedAt)+"</li>";
  }).join("")+"</ul>";
}

/** Client names and IDs are self-reported by the client: escaped text, never a link. */
function grantsSection(grants:OwnerGrant[],tokens:ReadonlyMap<string,string>):string{
  if(grants.length===0)return "<h4>Connected clients</h4><p>No connected clients.</p>";
  return "<h4>Connected clients</h4><ul>"+grants.map(grant=>"<li>"+
    (grant.clientName===null?"Unnamed client":esc(grant.clientName)+unverified)+"; client ID <code>"+esc(grant.clientId)+"</code>; connected "+
    esc(grant.createdAt)+form("Revoke access","revoke-grant:"+grant.grantId,tokens)+"</li>").join("")+"</ul>";
}

/** Apps the owner let manage every agent they own, later ones included. Names are self-reported: escaped text, never a link. */
function ownerGrantsSection(grants:readonly OwnerAppGrant[],tokens:ReadonlyMap<string,string>):string{
  if(grants.length===0)return "<p>No app manages all your agents.</p>";
  return "<ul>"+grants.map(grant=>"<li>"+(grant.clientName===null?"Unnamed client":esc(grant.clientName)+unverified)+"; client ID <code>"+esc(grant.clientId)+"</code>; connected "+
    esc(grant.createdAt)+form("Revoke access","revoke-grant:"+grant.grantId,tokens)+"</li>").join("")+"</ul>";
}

/**
 * What disconnecting a credential does to the actions waiting for approval on it: they can never be approved (the confirm answers
 * account-changed). Counted from the shown pending list only, so a cut list says "at least"; an action already flagged
 * accountChanged is not counted again.
 */
function disconnectWarning(agentId:string,credentialRef:string,pending:Pending):string{
  const count=pending.actions.filter(action=>action.agentId===agentId&&action.credentialRef===credentialRef&&!action.accountChanged).length;
  if(count===0)return "";
  return "<p>"+(pending.more?"At least ":"")+count+(count===1?" action waiting for your approval uses":" actions waiting for your approval use")+
    " this account. Disconnecting it means "+(count===1?"that action":"those actions")+" can never be approved or sent.</p>";
}
type Pending={actions:readonly OwnerExternalAction[];more:boolean};

/** The account each credential acts as is what the provider verified at connect time. Connect forms come from the platforms' own field lists. */
function credentialsSection(agentId:string,credentials:OwnerCredential[],connectors:OwnerConnector[],tokens:ReadonlyMap<string,string>,pending:Pending):string{
  const connect=form("Connect YouTube","connect-youtube:"+agentId,tokens);
  const list=credentials.length===0?"<p>No connected accounts.</p>":"<ul>"+credentials.map(credential=>"<li>"+esc(credential.provider)+" as "+
    esc(credential.targetDisplay)+" (<code>"+esc(credential.targetAccount)+"</code>); credential <code>"+esc(credential.credentialRef)+"@"+
    esc(credential.revision)+"</code>; connected "+esc(credential.connectedAt)+disconnectWarning(agentId,credential.credentialRef,pending)+
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

/** Where the owner page shows the agents after `after`; the first page has no cursor. */
export const agentsPagePath=(after:string|null)=>after===null?"/account":"/account?agents="+encodeURIComponent(after);
function agentSection(overview:Extract<OwnerOverview,{frozen:false}>,tokens:ReadonlyMap<string,string>,after:string|null):string{
  const paging=[after===null?"":'<a href="'+esc(agentsPagePath(null))+'">First agents</a>',
    overview.nextAgents===null?"":'<a href="'+esc(agentsPagePath(overview.nextAgents))+'">More agents</a>'].filter(Boolean).join(" · ");
  const nav=paging===""?"":"<p>"+paging+"</p>";
  if(overview.agents.length===0)return (after===null?"<p>You do not own any agents yet.</p>":"<p>No more agents.</p>")+nav;
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
    grantsSection(agent.grants,tokens),addressesSection(agent.addresses),credentialsSection(agent.agentId,agent.credentials,overview.connectors,tokens,
      {actions:overview.externalActions,more:overview.moreExternalActions===true}),
    "<h4>Lost every key?</h4><p>A recovery code lets the agent add a new key without any existing key. It replaces any earlier code.</p>",
    form("Issue recovery code","issue-recovery-code:"+agent.agentId,tokens),"</section>"
  ].join("")).join("")+nav;
}

/**
 * The owner's page, showing the page of agents after `after`. `tokens` maps each form to its signed action token; a form without
 * a token is not rendered.
 */
export function ownerPage(overview:OwnerOverview,human:HumanSession,tokens:ReadonlyMap<string,string>,result:string|null,now:number,after:string|null=null):Response{
  const shown=result!==null&&Object.hasOwn(ownerResultText,result)?ownerResultText[result]:null;
  const head=(shown===null?"":"<p role=\"status\">"+esc(shown)+"</p>")+"<h1>Your Weave agents</h1><p>Signed in as @"+esc(human.login)+"</p>";
  if(overview.frozen)return shell("Your Weave agents",head+"<p>This GitHub identity is frozen; contact the operator. Nothing can be changed from here.</p>");
  const fresh=freshAuthentication(human,now);
  return shell("Your Weave agents",[head,
    "<p>Confirming, approving an action, connecting an account, revoking a key or issuing a recovery code needs a GitHub sign-in in the last 10 minutes. "+(fresh?"Your current sign-in qualifies.":
      "Your current sign-in is older, so you will be asked to sign in again first.")+" Revoking a connected client's access or disconnecting an account does not.</p>",
    "<h2>Binding requests</h2>",bindingSection(overview.bindingRequests,tokens,now),
    "<h2>Actions waiting for approval</h2>",actionsSection(overview.externalActions,tokens),
    // A mailbox that predates these flags omits them, which reads as nothing left out.
    overview.moreExternalActions===true?"<p>Only the "+overview.externalActions.length+" oldest actions waiting for approval are shown. "+
      "Approve or reject some to see the rest here; each one's own page, where the app that proposed it links you, also works.</p>":"",
    "<h2>Recent actions</h2>",recentSection(overview.recentActions),
    overview.moreRecentActions===true?"<p>Only the "+overview.recentActions.length+" most recent are listed; older ones from the last 7 days are not shown here. "+
      "Each one's own page, where the app that proposed it links you, still says what became of it.</p>":"",
    "<h2>Apps that manage all your agents</h2>",
    "<p>Each can create, list and rename your agents and act as any of them, including agents you add later. Approving actions stays with you.</p>",
    ownerGrantsSection(overview.ownerGrants,tokens),
    "<h2>Your agents</h2>",agentSection(overview,tokens,after)].join(""));
}

/** The later state of a sent action whose effect the platform finishes after sending, such as a scheduled post. */
export type SettledPublicationView={state:string;url:string|null;error:string|null};
/**
 * What became of an action of the human's own agent that no longer waits for a decision: its status and stored failure code, and
 * once sent the platform's receipt and tracked publication. A mailbox that predates them omits them, which reads as none.
 */
export type SettledActionView={status:string;failure:string|null;receipt?:string|null;publication?:SettledPublicationView|null;about?:SettledPlanView|null;sendBy?:string|null};
/** What the settled action was: the agent, operation, the account the plan bound (its name while the credential still points there) and the content. */
export type SettledPlanView={agentId:string;agentDisplayName:string|null;provider:string;operation:string;targetAccount:string;targetDisplay:string|null;content:string};
/**
 * What the mailbox answers for one action of the signed-in human: the pending action to decide, or, for their own agent's action
 * that no longer waits, what became of it. A mailbox that predates `settled` omits it, which reads as unknown.
 */
export type PendingActionView={frozen:true}|{frozen:false;action:OwnerExternalAction|null;settled?:SettledActionView|null};
/** The address of the page for one action: what an app such as Envoylet links to so its owner can decide there. */
export const actionPagePath=(actionId:string)=>"/account/actions/"+actionId;
/** The outcome of a decision on the page for one action; failure reasons share the owner page's text. */
const actionResultText:Readonly<Record<string,string>>=Object.freeze({
  approved:"Approved. The agent may now send it once. You can close this page.",
  rejected:"Rejected. Nothing will be sent. You can close this page.",
  "not-pending":"This action is no longer waiting for a decision: it was already decided or expired, or the agent started sending it. Open it again to see what became of it."
});

/**
 * What the page for one action says about the owner's own action once it no longer waits for a decision. Read from the mailbox,
 * never from the URL. None of it repeats a decision outcome's wording, so it never reads as a decision this request made.
 */
const settledText:Readonly<Record<string,string>>=Object.freeze({
  approved:"You approved this action. Nothing is sent until the agent sends it, once, before it expires.",
  rejected:"This action was rejected. Nothing will be sent.",
  expired:"This action expired without being sent. Nothing will be sent.",
  dispatching:"Weave started sending this action and has not recorded the platform's answer. It will not be sent again.",
  completed:"This action was sent.",
  failed:"This action was not sent.",
  unknown:"Weave sent this action but could not confirm whether the platform accepted it. It will not be sent again; check the platform."
});

const reconnect="Disconnect the account, connect it again, then have your agent propose it again.";
/** What a not-sent action's stored failure code means and what the owner can do; the mailbox's re-checks at send time and two platform refusals. */
const failureText:Readonly<Record<string,string>>=Object.freeze({
  "owner-changed":"The agent's owner changed after the approval, so Weave did not send it.",
  "owner-frozen":"The owner's GitHub identity was frozen before it could be sent, so Weave did not send it.",
  "credential-changed":"The account it posts through was reconnected or disconnected after the approval, so Weave did not send it. Your agent can propose it again.",
  "credential-unreadable":"Weave could not open the stored sign-in for that account, so it did not send it. "+reconnect,
  "plan-changed":"Weave's connector for this platform changed after the approval, so the approved plan no longer applies and Weave did not send it. Your agent can propose it again.",
  "provider-rejected: credential-refused":"The platform refused the connected account's sign-in, so nothing was posted. "+reconnect,
  "provider-rejected: media-unavailable":"A file in it was no longer stored as approved, so nothing was posted. Your agent can upload it and propose it again."
});
const rejectedPrefix="provider-rejected: ";
/** The explanation for a stored failure code, or none for a code this page does not know: never guessed. */
function explainFailure(code:string):string|null{
  if(Object.hasOwn(failureText,code))return failureText[code]!;
  // Any other definite platform refusal: the platform said no, so nothing was posted.
  return code.startsWith(rejectedPrefix)&&code.length>rejectedPrefix.length?
    "The platform refused it, so nothing was posted.":null;
}

/**
 * Which action the settled page is about, so the owner who returns to it sees what it was: agent-supplied text, escaped and shown
 * verbatim. A mailbox that predates `about`, or a malformed one, adds nothing.
 */
function settledPlan(about:SettledPlanView|null|undefined):string{
  if(!about)return "";
  const {agentId,agentDisplayName,provider,operation,targetAccount,targetDisplay,content}=about;
  if(![agentId,provider,operation,targetAccount,content].every(value=>typeof value==="string"))return "";
  const display=typeof agentDisplayName==="string"?agentDisplayName:null,account=typeof targetDisplay==="string"?esc(targetDisplay)+" (<code>"+esc(targetAccount)+"</code>)":"<code>"+esc(targetAccount)+"</code>";
  return "<p>"+agentName(agentId,display)+" asked to "+esc(operation)+" on "+esc(provider)+" as "+account+".</p><p>Content, exactly as proposed:</p><pre>"+esc(content)+"</pre>";
}

/** A link the page may show for a published post: an absolute https URL only, so a stored value can never run script. */
const publishedLink=(url:string|null|undefined)=>typeof url==="string"&&url.startsWith("https://")&&URL.canParse(url)?url:null;

/**
 * What the page adds for the owner's sent action: the platform's reference, and for an effect the platform finishes later (a
 * scheduled post) whether it was published, with a link to it, or what the platform reported. All of it is read from the mailbox.
 */
function sentDetails(settled:SettledActionView):string{
  const parts:string[]=[];
  if(typeof settled.receipt==="string"&&settled.receipt)parts.push("<p>The platform's reference for it: <code>"+esc(settled.receipt)+"</code></p>");
  const publication=settled.publication;
  if(publication?.state==="queued")parts.push("<p>The platform has not reported it published yet. Nothing is sent again.</p>");
  else if(publication?.state==="published"){
    const url=publishedLink(publication.url);
    parts.push("<p>The platform published it."+(url?' <a href="'+esc(url)+'" rel="noopener noreferrer">View it on the platform</a>':"")+"</p>");
  }else if(publication?.state==="failed")
    parts.push("<p>The platform reported that publishing failed"+(publication.error?": "+esc(publication.error):"")+". Weave will not send it again.</p>");
  return parts.join("");
}

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
 * owner page, posting back to this address. The owner's own action that no longer waits says what became of it; one that
 * belongs to someone else, or is unknown, is described the same way as any action not waiting for this human.
 */
export function actionPage(view:PendingActionView,human:HumanSession,actionId:string,tokens:ReadonlyMap<string,string>,now:number):Response{
  const head="<h1>Approve an action</h1><p>Signed in as @"+esc(human.login)+"</p>";
  if(view.frozen)return shell("Approve an action",head+"<p>This GitHub identity is frozen; contact the operator. Nothing can be changed from here.</p>");
  const action=view.action?.actionId===actionId?view.action:null;
  const all='<p><a href="/account">All your agents and actions</a></p>';
  if(!action){
    const settled=view.settled&&Object.hasOwn(settledText,view.settled.status)?view.settled:null;
    const failure=settled?.status==="failed"&&settled.failure?settled.failure:null,why=failure===null?null:explainFailure(failure);
    const text=settled?settledText[settled.status]+(failure===null?"":" Reason: "+esc(failure)+"."+(why===null?"":" "+why)):
      "This action is not waiting for your approval. It may already be decided or expired.";
    // An approval the agent has not used yet can still be withdrawn here; the mailbox refuses it once sending has started.
    // When an approval runs out, read from the mailbox; a mailbox that predates `sendBy`, or a malformed one, adds nothing.
    const sendBy=settled?.status==="approved"&&typeof settled.sendBy==="string"&&settled.sendBy?"<p>The agent must send it by "+esc(settled.sendBy)+". After that it expires and is never sent.</p>":"";
    const withdraw=settled?.status==="approved"?form("Withdraw approval","reject-action:"+actionId,tokens,actionPagePath(actionId)):"";
    return shell("Approve an action",head+"<p>"+text+"</p>"+sendBy+(withdraw===""?"":"<p>Changed your mind? Withdraw the approval and it will never be sent. Once the agent has started sending it, it can no longer be withdrawn.</p>"+withdraw)+
      (settled?.status==="completed"?sentDetails(settled):"")+(settled?settledPlan(settled.about):"")+all);
  }
  const fresh=freshAuthentication(human,now);
  return shell("Approve an action",[head,
    action.accountChanged?"":"<p>Approving needs a GitHub sign-in in the last 10 minutes. "+(fresh?"Your current sign-in qualifies.":"You will be asked to sign in again first.")+"</p>",
    actionsSection([action],tokens,actionPagePath(actionId)),all].join(""));
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
    if(!action.accountChanged)await add("confirm-action:"+action.actionId,{kind:"confirm-action",requestId:action.actionId,planDigest:action.planDigest});
    await add("reject-action:"+action.actionId,{kind:"reject-action",requestId:action.actionId});
  }
  for(const g of overview.ownerGrants)await add("revoke-grant:"+g.grantId,{kind:"revoke-grant",grantId:g.grantId});
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
