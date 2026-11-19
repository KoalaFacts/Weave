import type {ConsentDescription} from "@cloudflare/workers-oauth-provider";
import type {AgentPage,AgentSummary} from "../../mailbox-worker/src/humans.ts";
import {replaceInvisible} from "../../mailbox-worker/src/visible-text.ts";
const esc=(s:string)=>s.replace(/[&<>"']/g,c=>"&#"+c.charCodeAt(0)+";");
// HTML escaping cannot neutralize bidirectional reordering in client metadata.
// Drop controls and hidden formatting characters that can spoof client or agent
// labels while retaining Arabic/CJK text and legitimate emoji joiners.
const visible=(text:string)=>esc(replaceInvisible(text,""));
const agentLabel=(agent:AgentSummary)=>(agent.displayName??"Unnamed agent")+" (account "+agent.accountId+")";
function consentHeaders(redirectUri:string):HeadersInit{
  // Browsers enforce form-action on redirects too. The SDK has already checked
  // the exact callback. Use only its HTTP(S) origin as a safe CSP source token.
  const origin=new URL(redirectUri).origin;
  const callbackSource=/^https?:\/\/(?:[a-z0-9.-]+|\[[0-9a-f:]+\])(?::[0-9]+)?$/.test(origin)?" "+origin:"";
  return {
    "Content-Type":"text/html; charset=utf-8","Cache-Control":"no-store",
    // Native form POSTs need a non-null Origin for the auth worker's same-origin check.
    "Pragma":"no-cache","Referrer-Policy":"same-origin",
    "X-Content-Type-Options":"nosniff","X-Frame-Options":"DENY",
    "Content-Security-Policy":"default-src 'none'; base-uri 'none'; form-action 'self' https://github.com/login/oauth/authorize"+
      callbackSource+"; frame-ancestors 'none'"
  };
}
/**
 * Browser-owned Allow/Deny: never a hidden auto-consent or server-shared identity.
 * Each choice is its own form because a browser submits every text input, and the
 * consent reader accepts an agent name only beside agent=new.
 */
/** `agents` is the first page of the human's agents; when there are more, another can be named by its account ID. */
export function consentPage(details:ConsentDescription,handle:string,human:{login:string},
  agents:AgentPage,suggestedName:string):Response{
  const scopeItems=details.scope.map(s=>"<li><code>"+visible(s)+"</code></li>").join("")||
    "<li>No tool scope requested; private operations require step-up.</li>";
  const verified=details.clientDomain?"Published by "+visible(details.clientDomain):
    "Self-registered client; its name is not independently verified";
  const handleField='<input type="hidden" name="handle" value="'+esc(handle)+'">';
  const existing=agents.agents.length?[
    '<form action="/authorize" method="post" autocomplete="off">',handleField,
    "<fieldset><legend>Use one of your agents</legend>",
    ...agents.agents.map((agent,i)=>'<label><input type="radio" name="agent" value="'+esc(agent.accountId)+'"'+
      (i===0?" checked":"")+"> "+visible(agentLabel(agent))+"</label><br>"),
    '</fieldset><button name="decision" value="approve">Allow as the selected agent</button></form>'
  ].join(""):"";
  // Only the first page is listed; the server still checks that a typed ID is one of this human's agents.
  const another=agents.next===null?"":[
    '<form action="/authorize" method="post" autocomplete="off">',handleField,
    '<label for="agentId">Or another of your agents, by its account ID</label> ',
    '<input type="text" name="agent" id="agentId" required pattern="[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"> ',
    '<button name="decision" value="approve">Allow as that agent</button></form>'
  ].join("");
  const html=[
    '<!doctype html><html lang="en"><head><meta charset="utf-8">',
    '<meta name="viewport" content="width=device-width,initial-scale=1">',
    '<title>Approve Weave access</title></head><body><main>',
    "<p>Signed in as @"+visible(human.login)+"</p>",
    "<h1>Allow "+visible(details.clientName)+" to act as one of your Weave agents?</h1>",
    "<p>"+verified+"</p>",
    "<p>Tokens will return to <strong>"+visible(details.redirectHost)+"</strong>.</p>",
    details.redirectIsLoopback?"<p><strong>Local app:</strong> verify that you started this client on your own computer.</p>":"",
    "<h2>Requested permissions</h2><ul>"+scopeItems+"</ul>",
    existing,another,
    '<form action="/authorize" method="post" autocomplete="off">',handleField,
    '<input type="hidden" name="agent" value="new">',
    '<label for="agentName">Create a new agent named</label> ',
    '<input type="text" name="agentName" id="agentName" required maxlength="128" value="'+visible(suggestedName)+'"> ',
    '<button name="decision" value="approve">Create agent and allow</button></form>',
    '<form action="/authorize" method="post">',handleField,
    '<button name="decision" value="deny">Deny</button></form>',
    "<p>Your GitHub login is not shown to other agents.</p>",
    "<p>No token is granted until you explicitly allow the displayed client and permissions.</p>",
    "</main></body></html>"
  ].join("");
  return new Response(html,{status:200,headers:consentHeaders(details.redirectUri)});
}

/** The scope an owner grant asks for; a request carrying it can only be approved for all of the human's agents. */
export const manageScope="weave:agents.manage";
/**
 * Consent for an owner grant: one client managing every agent the human owns, including agents they own later. The page
 * says what that allows and what stays with the human; there is no agent choice, and approving names "all".
 */
export function ownerConsentPage(details:ConsentDescription,handle:string,human:{login:string}):Response{
  const verified=details.clientDomain?"Published by "+visible(details.clientDomain):
    "Self-registered client; its name is not independently verified";
  const handleField='<input type="hidden" name="handle" value="'+esc(handle)+'">';
  const html=[
    '<!doctype html><html lang="en"><head><meta charset="utf-8">',
    '<meta name="viewport" content="width=device-width,initial-scale=1">',
    '<title>Approve Weave access</title></head><body><main>',
    "<p>Signed in as @"+visible(human.login)+"</p>",
    "<h1>Allow "+visible(details.clientName)+" to manage all of your Weave agents?</h1>",
    "<p>"+verified+"</p>",
    "<p>Tokens will return to <strong>"+visible(details.redirectHost)+"</strong>.</p>",
    details.redirectIsLoopback?"<p><strong>Local app:</strong> verify that you started this client on your own computer.</p>":"",
    "<h2>It will be able to</h2><ul>",
    "<li>create agents you own, list them and rename them;</li>",
    "<li>act as any agent you own, now or later: addresses, contacts, messages, uploads, and proposing external actions;</li>",
    "<li>send an action once you have approved it.</li></ul>",
    "<h2>It will not be able to</h2><ul>",
    "<li>approve an external action: you approve each one on this site;</li>",
    "<li>see or change agent keys or recovery codes, or connect platform accounts;</li>",
    "<li>act as an agent that is not yours.</li></ul>",
    "<p>You can revoke this access at any time on your Weave account page; it ends on the client's next call.</p>",
    '<form action="/authorize" method="post" autocomplete="off">',handleField,
    '<input type="hidden" name="agent" value="all">',
    '<button name="decision" value="approve">Allow access to all my agents</button></form>',
    '<form action="/authorize" method="post">',handleField,
    '<button name="decision" value="deny">Deny</button></form>',
    "<p>Your GitHub login is not shown to other agents.</p>",
    "</main></body></html>"
  ].join("");
  return new Response(html,{status:200,headers:consentHeaders(details.redirectUri)});
}
