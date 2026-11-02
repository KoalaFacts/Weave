import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
const moduleUrl=new URL('../scripts/verify-agent-entry.ts',import.meta.url);
async function verifier(){assert.ok(existsSync(moduleUrl),'Post-deploy verification script is missing');return (await import(moduleUrl)).verifyAgentEntry;}
const home='<a href="/agent-start.md">Guide</a><a href="/connect">Connect agent (GitHub + MCP)</a><section id="agentStart"></section><textarea id="invitationCard"></textarea>';
const connect='<section id="mcpConnect"><pre>claude mcp add --transport http weave https://demo.weave-389.workers.dev/mcp</pre><pre>codex mcp login weave</pre><a href="https://weave-auth-demo.weave-389.workers.dev/account">Owner</a></section>';
function fixture(url: string,mode='valid'){
    const path=new URL(url).pathname;
    let value=path==='/agent-start.md'?'canonical guide':path==='/connect'?connect:home;
    if(mode==='old-guide'&&path==='/agent-start.md')value='old guide';
    if(mode==='missing-entry'&&path==='/')value='old homepage';
    if(mode==='missing-home-link'&&path==='/')value=home.replace('<a href="/connect">Connect agent (GitHub + MCP)</a>','');
    if(mode==='missing-connect'&&path==='/connect')value='old connect page';
    if(mode==='oversized'&&path==='/agent-start.md')value='x'.repeat(262145);
    return new Response(value,{
        status:mode==='http-error'&&path==='/connect'?503:200,
        headers:{
            'content-type':mode==='wrong-type'&&path==='/connect'?'application/json':path.endsWith('.md')?'text/markdown; charset=utf-8':'text/html; charset=utf-8',
            'cache-control':'no-store',
            'x-content-type-options':'nosniff'
        }
    });
}
test('release verification checks exact guide and live OAuth connect page with anonymous read-only requests',async()=>{
    const verify=await verifier(),requests=[];
    const result=await verify({expectedGuide:'canonical guide',fetchImpl:async(url,options)=>{requests.push({url,options});return fixture(url);}});
    assert.match(result.guideSha256,/^[a-f0-9]{64}$/);
    assert.match(result.connectSha256,/^[a-f0-9]{64}$/);
    assert.equal(requests.length,3);
    for(const {url,options}of requests){
        assert.ok(url.startsWith('https://demo.weave-389.workers.dev/'));
        assert.equal(options.method,'GET');
        assert.equal(options.redirect,'error');
        assert.equal(options.headers,undefined);
    }
});
for(const mode of ['old-guide','wrong-type','http-error','missing-entry','missing-home-link','missing-connect','oversized'])
    test('release verification rejects '+mode,async()=>{
        const verify=await verifier();
        await assert.rejects(verify({expectedGuide:'canonical guide',fetchImpl:async url=>fixture(url,mode)}));
    });
