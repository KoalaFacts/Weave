import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
const moduleUrl=new URL('../scripts/verify-agent-entry.ts',import.meta.url);
async function verifier(){assert.ok(existsSync(moduleUrl),'Post-deploy verification script is missing');return (await import(moduleUrl)).verifyAgentEntry;}
const home='<a href="/agent-start.md">Guide</a><section id="agentStart"></section><textarea id="invitationCard"></textarea>';
test('release verification checks exact guide contents with anonymous read-only requests',async()=>{
    const verify=await verifier(),requests=[];
    const result=await verify({expectedGuide:'canonical guide',fetchImpl:async(url,options)=>{requests.push({url,options});return new Response(url.endsWith('/agent-start.md')?'canonical guide':home,{headers:{'content-type':url.endsWith('.md')?'text/markdown; charset=utf-8':'text/html; charset=utf-8','cache-control':'no-store','x-content-type-options':'nosniff'}});}});
    assert.match(result.guideSha256,/^[a-f0-9]{64}$/);assert.equal(requests.length,2);
    for(const {url,options}of requests){assert.ok(url.startsWith('https://demo.weave-389.workers.dev/'));assert.equal(options.method,'GET');assert.equal(options.redirect,'error');assert.equal(options.headers,undefined);}
});
for(const mode of ['old-guide','wrong-type','http-error','missing-entry','oversized'])test('release verification rejects '+mode,async()=>{
    const verify=await verifier();
    await assert.rejects(verify({expectedGuide:'canonical guide',fetchImpl:async url=>new Response(mode==='oversized'?'x'.repeat(262145):url.endsWith('.md')?(mode==='old-guide'?'old guide':'canonical guide'):(mode==='missing-entry'?'old homepage':home),{status:mode==='http-error'?503:200,headers:{'content-type':mode==='wrong-type'?'text/html':url.endsWith('.md')?'text/markdown':'text/html','cache-control':'no-store','x-content-type-options':'nosniff'}})}));
});
