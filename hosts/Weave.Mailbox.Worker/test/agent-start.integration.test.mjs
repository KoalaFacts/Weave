import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile } from 'node:fs/promises';
import { Script } from 'node:vm';
import { start, secret } from './harness.mjs';
import { mcpClient, value } from './mcp-client.mjs';

test('anonymous agent entry serves the canonical guide and inert nonce-protected onboarding',async()=>{
    const h=await start();
    try{
        const response=await h.request('/agent-start.md');assert.equal(response.status,200);
        assert.match(response.headers.get('content-type'),/^text\/markdown/);assert.equal(response.headers.get('cache-control'),'no-store');assert.equal(response.headers.get('x-content-type-options'),'nosniff');
        assert.equal(await response.text(),await readFile('src/agent-start.md','utf8'));
        const home=await h.request('/'),html=await home.text(),csp=home.headers.get('content-security-policy');
        assert.ok(html.indexOf('href="/agent-start.md"')<html.indexOf('id="accountHeading"'));
        for(const role of ['A','B']){
            const prompt=new RegExp('<textarea id="agentPrompt'+role+'"[^>]*>([\\s\\S]*?)</textarea>').exec(html)[1];
            assert.match(prompt,/agent-start\.md/);assert.match(prompt,/runId/);assert.doesNotMatch(prompt,/ping 001|pong 001/);
        }
        const scripts=[...html.matchAll(/<script nonce="([^"]+)">([\s\S]*?)<\/script>/g)];assert.equal(scripts.length,2);
        for(const [,nonce,script]of scripts){assert.ok(csp.includes("'nonce-"+nonce+"'"));new Script(script);}
        assert.ok(!html.includes('WEAVE_NONCE'));assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records WHERE kind='account'").first('n'),0);
        const contract=await h.ok('/openapi.json');
        for(const path of ['/v1/accounts','/v1/addresses','/v1/contacts/channel','/v1/contacts/requests','/v1/contacts/requests/{requestId}','/v1/messages','/v1/inbox','/v1/inbox/{messageId}/ack','/v1/outbox/{messageId}'])assert.ok(contract.paths[path],path);
    }finally{await h.close();}
});
test('MCP guide returns the exact same instructions without registration or secret disclosure',async()=>{
    const h=await start(),control=secret(),client=mcpClient(String(await h.mf.ready),control);
    try{
        await client.initialize();const result=await client.call('demo_guide'),guide=value(result);
        assert.equal(guide.instructions,await readFile('src/agent-start.md','utf8'));assert.equal(guide.start,guide.origin+'/agent-start.md');
        assert.ok(guide.boundary.includes('does not grant permission'));assert.equal(guide.readiness.credentialConfigured,true);
        assert.ok(!JSON.stringify(result).includes(control));assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records WHERE kind='account'").first('n'),0);
    }finally{await client.close();await h.close();}
});
