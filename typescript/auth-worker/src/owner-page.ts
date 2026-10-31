import type {OwnerBindingRequest,OwnerKey,OwnerOverview} from "../../mailbox-worker/src/owner-actions.ts";
import {freshAuthentication,type HumanSession} from "./human-session.ts";
import {ownerActionToken} from "./owner-forms.ts";

const esc=(s:string)=>s.replace(/[&<>"']/g,c=>"&#"+c.charCodeAt(0)+";");
const headers={
  "Content-Type":"text/html; charset=utf-8","Cache-Control":"no-store","Pragma":"no-cache","Referrer-Policy":"no-referrer",
  "X-Content-Type-Options":"nosniff","X-Frame-Options":"DENY",
  "Content-Security-Policy":"default-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'"
};
const shell=(title:string,body:string,status=200)=>new Response(
  '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>'+
  esc(title)+"</title></head><body><main>"+body+"</main></body></html>",{status,headers});

/** Text for `?result=`: the success and each failure reason the mailbox worker can return. Anything else is ignored. */
export const ownerResultText:Readonly<Record<string,string>>=Object.freeze({
  done:"Done.",
  "stale-authentication":"Sign in with GitHub again, then retry: this needs a sign-in from the last 10 minutes.",
  frozen:"This GitHub identity is frozen; contact the operator.",
  "not-found":"That request or key was not found.",
  "not-pending":"That request was already decided or cancelled.",
  expired:"That request has expired.",
  "agent-limit":"You already own the maximum of 10 agents.",
  "already-owned":"That agent already belongs to a person.",
  "key-in-use":"That key is already in use.",
  "key-limit":"The agent already has the maximum number of live keys.",
  "endorser-revoked":"The key that endorsed this request has been revoked.",
  "requesting-key-revoked":"The key that made this request has been revoked.",
  unavailable:"Weave could not complete that right now. Try again later."
});

/** The 43-character key ID in full, its first eight characters emphasised for comparison with what the agent shows. */
const fingerprint=(kid:string)=>"<code><strong>"+esc(kid.slice(0,8))+"</strong>"+esc(kid.slice(8))+"</code>";
const unverified=" (self-reported, not verified)";
const agentName=(agentId:string,displayName:string|null)=>
  displayName===null?"Unnamed agent "+esc(agentId.slice(0,8)):esc(displayName)+unverified;
const form=(label:string,key:string,tokens:ReadonlyMap<string,string>)=>{
  const token=tokens.get(key);
  return token===undefined?"":'<form method="post" action="/account"><input type="hidden" name="action" value="'+esc(token)+
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
      (request.endorsedBy===null?"recovery request - no existing key signed it":"endorsed by "+fingerprint(request.endorsedBy))+
      "; expires "+esc(request.expiresAt)+form("Confirm","confirm-key:"+request.requestId,tokens)+
      form("Reject","reject-key:"+request.requestId,tokens)+"</li>").join("")+"</ul>"),
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
    "<p>Confirming or revoking needs a GitHub sign-in in the last 10 minutes. "+(fresh?"Your current sign-in qualifies.":
      "Your current sign-in is older, so you will be asked to sign in again first.")+"</p>",
    "<h2>Binding requests</h2>",bindingSection(overview.bindingRequests,tokens,now),
    "<h2>Your agents</h2>",agentSection(overview,tokens)].join(""));
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
export async function ownerFormTokens(key:string,humanId:string,overview:OwnerOverview,now:number):Promise<Map<string,string>>{
  const tokens=new Map<string,string>();
  if(overview.frozen)return tokens;
  const add=async(name:string,action:Parameters<typeof ownerActionToken>[2])=>{tokens.set(name,await ownerActionToken(key,humanId,action,now));};
  for(const request of overview.bindingRequests){
    await add("confirm-binding:"+request.requestId,{kind:"confirm-binding",requestId:request.requestId});
    await add("reject-binding:"+request.requestId,{kind:"reject-binding",requestId:request.requestId});
  }
  for(const agent of overview.agents){
    await add("issue-recovery-code:"+agent.agentId,{kind:"issue-recovery-code",agentId:agent.agentId});
    for(const k of agent.keys)await add("revoke-key:"+agent.agentId+":"+k.kid,{kind:"revoke-key",agentId:agent.agentId,kid:k.kid});
    for(const r of agent.keyRequests){
      await add("confirm-key:"+r.requestId,{kind:"confirm-key",requestId:r.requestId});
      await add("reject-key:"+r.requestId,{kind:"reject-key",requestId:r.requestId});
    }
  }
  return tokens;
}
