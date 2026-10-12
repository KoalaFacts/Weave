import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Script } from 'node:vm';
import { start } from './harness.mjs';
test('demo entry serves executable UI with per-response CSP nonce, no cached credential or external script',async()=>{
    const h=await start();
    try {
        const response=await h.request('/');assert.equal(response.status,200);
        assert.equal(response.headers.get('cache-control'),'no-store');
        const csp=response.headers.get('content-security-policy'),html=await response.text();
        const script=/<script nonce="([^"]+)">([\s\S]*?)<\/script>/.exec(html);assert.ok(script);assert.ok(csp.includes("'nonce-"+script[1]+"'"));
        new Script(script[2]);
        assert.ok(csp.includes("connect-src 'self'"));assert.ok(csp.includes("frame-ancestors 'none'"));
        assert.equal(/localStorage|sessionStorage|document\.cookie/.test(script[2]),false);
        for(const id of ['register','createAddress','discover','requestContact','loadContacts','send','loadInbox','stream'])assert.ok(html.includes('id="'+id+'"'));
        const second=await h.request('/');assert.notEqual(second.headers.get('content-security-policy'),csp);
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
