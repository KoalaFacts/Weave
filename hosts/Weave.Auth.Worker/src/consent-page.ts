import type {ConsentDescription} from "@cloudflare/workers-oauth-provider";
const esc=(s:string)=>s.replace(/[&<>"']/g,c=>"&#"+c.charCodeAt(0)+";");
/** Browser-owned Allow/Deny: never a hidden auto-consent or server-shared identity. */
export function consentPage(details:ConsentDescription,handle:string,proofUrl:string):Response{
  const scopeItems=details.scope.map(s=>"<li><code>"+esc(s)+"</code></li>").join("")||
    "<li>No tool scope requested; private operations require step-up.</li>";
  const verified=details.clientDomain?"Published by "+esc(details.clientDomain):
    "Self-registered client; its name is not independently verified";
  const html=[
    '<!doctype html><html lang="en"><head><meta charset="utf-8">',
    '<meta name="viewport" content="width=device-width,initial-scale=1">',
    '<title>Approve Weave access</title></head><body><main>',
    "<h1>Allow "+esc(details.clientName)+" to access your Weave account?</h1>",
    "<p>"+verified+"</p>",
    "<p>Tokens will return to <strong>"+esc(details.redirectHost)+"</strong>.</p>",
    details.redirectIsLoopback?"<p><strong>Local app:</strong> verify that you started this client on your own computer.</p>":"",
    "<h2>Requested permissions</h2><ul>"+scopeItems+"</ul>",
    "<p>Use your existing Weave control ONLY on Weave's own site, not this authorization server.</p>",
    '<p><a rel="noopener noreferrer" target="_blank" href="'+esc(proofUrl)+'">Get signed ownership proof from Weave</a></p>',
    "<p>Paste only the 60-second signed proof below. Never paste your account control here.</p>",
    '<form action="/authorize" method="post" autocomplete="off">',
    '<input type="hidden" name="handle" value="'+esc(handle)+'">',
    '<label for="proof">Signed ownership proof</label><br>',
    '<textarea name="proof" id="proof" rows="5" cols="68" maxlength="2048" spellcheck="false"></textarea><br>',
    '<button name="decision" value="approve">Allow requested permissions</button>',
    '<button name="decision" value="deny">Deny</button></form>',
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
