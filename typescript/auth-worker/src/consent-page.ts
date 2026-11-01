import type {ConsentDescription} from "@cloudflare/workers-oauth-provider";
import type {AgentSummary} from "../../mailbox-worker/src/humans.ts";
const esc=(s:string)=>s.replace(/[&<>"']/g,c=>"&#"+c.charCodeAt(0)+";");
// HTML escaping cannot neutralize bidirectional reordering in client metadata.
// Drop controls and hidden formatting characters that can spoof client or agent\n// labels while retaining Arabic/CJK text and legitimate emoji joiners.
const invisibleControls=/[\u0000-\u001f\u007f-\u009f\u00ad\u061c\u200b\u200e\u200f\u202a-\u202e\u2060\u2066-\u2069\ufeff]/gu;
const visible=(text:string)=>esc(text.replace(invisibleControls,""));
const agentLabel=(agent:AgentSummary)=>(agent.displayName??"Unnamed agent")+" (account "+agent.accountId+")";
/**
 * Browser-owned Allow/Deny: never a hidden auto-consent or server-shared identity.
 * Each choice is its own form because a browser submits every text input, and the
 * consent reader accepts an agent name only beside agent=new.
 */
export function consentPage(details:ConsentDescription,handle:string,human:{login:string},
  agents:AgentSummary[],suggestedName:string):Response{
  const scopeItems=details.scope.map(s=>"<li><code>"+visible(s)+"</code></li>").join("")||
    "<li>No tool scope requested; private operations require step-up.</li>";
  const verified=details.clientDomain?"Published by "+visible(details.clientDomain):
    "Self-registered client; its name is not independently verified";
  const handleField='<input type="hidden" name="handle" value="'+esc(handle)+'">';
  const existing=agents.length?[
    '<form action="/authorize" method="post" autocomplete="off">',handleField,
    "<fieldset><legend>Use one of your agents</legend>",
    ...agents.map((agent,i)=>'<label><input type="radio" name="agent" value="'+esc(agent.accountId)+'"'+
      (i===0?" checked":"")+"> "+visible(agentLabel(agent))+"</label><br>"),
    '</fieldset><button name="decision" value="approve">Allow as the selected agent</button></form>'
  ].join(""):"";
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
    existing,
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
  return new Response(html,{status:200,headers:{
    "Content-Type":"text/html; charset=utf-8","Cache-Control":"no-store",
    "Pragma":"no-cache","Referrer-Policy":"no-referrer",
    "X-Content-Type-Options":"nosniff","X-Frame-Options":"DENY",
    "Content-Security-Policy":"default-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'"
  }});
}
