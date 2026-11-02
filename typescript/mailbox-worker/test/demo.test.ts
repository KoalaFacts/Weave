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
        assert.match(html,/href="\/connect">Connect agent \(GitHub \+ MCP\)<\/a>/);
        assert.doesNotMatch(html,/authorization server that is not deployed yet/i);
        const connection=await h.request('/connect');assert.equal(connection.status,200);
        assert.equal(connection.headers.get('cache-control'),'no-store');
        assert.equal(connection.headers.get('referrer-policy'),'no-referrer');
        const csp=connection.headers.get('content-security-policy');
        assert.ok(csp.includes("default-src 'none'"));
        assert.ok(csp.includes("frame-ancestors 'none'"));
        const connectHtml=await connection.text();
        const nonce=/<style nonce="([^"]+)">/.exec(connectHtml)?.[1];
        assert.ok(nonce && csp.includes("'nonce-"+nonce+"'"));
        assert.match(connectHtml,/<section id="mcpConnect" aria-labelledby="mcpConnectHeading">/);
        assert.match(connectHtml,/href="https:\/\/weave-auth-demo\.weave-389\.workers\.dev\/account"/);
        assert.match(connectHtml,/claude mcp add --transport http weave https:\/\/demo\.weave-389\.workers\.dev\/mcp/);
        assert.match(connectHtml,/codex mcp add weave --url https:\/\/demo\.weave-389\.workers\.dev\/mcp/);
        assert.match(connectHtml,/codex mcp login weave/);
        assert.match(connectHtml,/separate GitHub sign-in and consent/i);
        assert.match(connectHtml,/browser key-based account is not automatically linked/i);
        assert.doesNotMatch(connectHtml,/authorization server that is not deployed yet/i);
        const second=await h.request('/connect');
        assert.notEqual(second.headers.get('content-security-policy'),csp);
        assert.equal((await h.mf.dispatchFetch('http://relay.test/connect')).status,400);
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
