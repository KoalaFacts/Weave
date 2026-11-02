import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Script } from 'node:vm';
import { start } from './harness.ts';
test('demo entry serves executable UI with per-response CSP nonce, no cached credential or external script',async()=>{
    const h=await start();
    try {
        const response=await h.request('/');assert.equal(response.status,200);
        assert.equal(response.headers.get('cache-control'),'no-store');
        const csp=response.headers.get('content-security-policy'),html=await response.text();
        const script=/<script nonce="([^"]+)">([\s\S]*?)<\/script>/.exec(html);assert.ok(script);assert.ok(csp.includes("'nonce-"+script[1]+"'"));
        new Script(script[2]);
        assert.ok(csp.includes("connect-src 'self'"));assert.ok(csp.includes("frame-ancestors 'none'"));
        assert.equal(/localStorage|sessionStorage|indexedDB|document\.cookie|console\./.test(script[2]),false);
        for(const id of ['createAccount','register','downloadCredential','createAddress','discover','requestContact','loadContacts','send','loadInbox','stream'])assert.ok(html.includes('id="'+id+'"'));
        assert.match(html,/<div id="credentialBackup" hidden>/);
        assert.match(html,/<details id="recovery">[\s\S]*?id="register"[\s\S]*?<\/details>/);
        assert.match(html,/<details id="addressDeveloper">[\s\S]*?id="directUri"[\s\S]*?<\/details>/);
        assert.match(html,/<a href="\/openapi.json">OpenAPI document<\/a>/);
        const second=await h.request('/');assert.notEqual(second.headers.get('content-security-policy'),csp);
    } finally {await h.close();}
});
test('demo presents a real GitHub OAuth path for two independent MCP agents',async()=>{
    const h=await start();
    try {
        const response=await h.request('/');assert.equal(response.status,200);
        const html=await response.text();
        assert.match(html,/<section id="mcpConnect" aria-labelledby="mcpConnectHeading">/);
        assert.match(html,/href="https:\/\/weave-auth-demo\.weave-389\.workers\.dev\/account"/);
        assert.match(html,/claude mcp add --transport http weave https:\/\/demo\.weave-389\.workers\.dev\/mcp/);
        assert.match(html,/codex mcp add weave --url https:\/\/demo\.weave-389\.workers\.dev\/mcp/);
        assert.match(html,/codex mcp login weave/);
        assert.match(html,/separate GitHub sign-in and consent/i);
        assert.match(html,/browser key-based account is not automatically linked/i);
        assert.doesNotMatch(html,/authorization server that is not deployed yet/i);
    } finally {await h.close();}
});
test('remote ingress requires HTTPS while local loopback remains usable',async()=>{
    const h=await start();
    try {
        for(const path of ['/','/v1/directory']) {
            assert.equal((await h.mf.dispatchFetch('http://relay.test'+path)).status,400);
            assert.equal((await h.mf.dispatchFetch('https://relay.test'+path)).status,200);
            assert.equal((await h.request(path)).status,200);
        }
    } finally {await h.close();}
});
