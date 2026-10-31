import type {ConsentDescription} from "@cloudflare/workers-oauth-provider";
import type {AgentSummary} from "../../mailbox-worker/src/humans.ts";
const esc=(s:string)=>s.replace(/[&<>"']/g,c=>"&#"+c.charCodeAt(0)+";");
const agentLabel=(agent:AgentSummary)=>agent.displayName??"Unnamed agent "+agent.accountId.slice(0,8);
/**
 * Browser-owned Allow/Deny: never a hidden auto-consent or server-shared identity.
 * Each choice is its own form because a browser submits every text input, and the
 * consent reader accepts an agent name only beside agent=new.
 */
export function consentPage(details:ConsentDescription,handle:string,human:{login:string},
  agents:AgentSummary[],suggestedName:string):Response{
  const scopeItems=details.scope.map(s=>"<li><code>"+esc(s)+"</code></li>").join("")||
    "<li>No tool scope requested; private operations require step-up.</li>";
  const verified=details.clientDomain?"Published by "+esc(details.clientDomain):
    "Self-registered client; its name is not independently verified";
  const handleField='<input type="hidden" name="handle" value="'+esc(handle)+'">';
  const existing=agents.length?[
    '<form action="/authorize" method="post" autocomplete="off">',handleField,
    "<fieldset><legend>Use one of your agents</legend>",
    ...agents.map((agent,i)=>'<label><input type="radio" name="agent" value="'+esc(agent.accountId)+'"'+
      (i===0?" checked":"")+"> "+esc(agentLabel(agent))+"</label><br>"),
    '</fieldset><button name="decision" value="approve">Allow as the selected agent</button></form>'
  ].join(""):"";
  const html=[
    '<!doctype html><html lang="en"><head><meta charset="utf-8">',
    '<meta name="viewport" content="width=device-width,initial-scale=1">',
    '<title>Approve Weave access</title></head><body><main>',
    "<p>Signed in as @"+esc(human.login)+"</p>",
    "<h1>Allow "+esc(details.clientName)+" to act as one of your Weave agents?</h1>",
    "<p>"+verified+"</p>",
    "<p>Tokens will return to <strong>"+esc(details.redirectHost)+"</strong>.</p>",
    details.redirectIsLoopback?"<p><strong>Local app:</strong> verify that you started this client on your own computer.</p>":"",
    "<h2>Requested permissions</h2><ul>"+scopeItems+"</ul>",
    existing,
    '<form action="/authorize" method="post" autocomplete="off">',handleField,
    '<input type="hidden" name="agent" value="new">',
    '<label for="agentName">Create a new agent named</label> ',
    '<input type="text" name="agentName" id="agentName" required maxlength="128" value="'+esc(suggestedName)+'"> ',
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
