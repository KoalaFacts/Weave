import assert from 'node:assert/strict';
import {test} from 'node:test';
import {start} from './harness.ts';

const clientId = 'https://demo.weave-389.workers.dev/oauth-client-metadata.json';

test('canonical public demo serves a safe first-party OAuth CIMD for independent loopback PKCE clients', async () => {
    const h = await start();
    try {
        const previous = await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
        const response = await h.mf.dispatchFetch(clientId, {headers:{Accept:'application/json'}});
        assert.equal(response.status, 200, await response.clone().text());
        assert.match(response.headers.get('Content-Type')??'', /^application\/json(?:;|$)/i);
        assert.equal(response.headers.get('X-Content-Type-Options'), 'nosniff');
        const metadata = await response.json() as Record<string,unknown>;
        assert.equal(metadata.client_id, clientId);
        assert.equal(typeof metadata.client_name, 'string');
        assert.ok((metadata.client_name as string).length>0);
        assert.deepEqual(metadata.redirect_uris, ['http://127.0.0.1/callback']);
        assert.deepEqual(metadata.grant_types, ['authorization_code']);
        assert.deepEqual(metadata.response_types, ['code']);
        assert.equal(metadata.token_endpoint_auth_method, 'none');
        assert.equal(metadata.client_secret, undefined);
        assert.equal(await h.db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token'), previous);
    } finally { await h.close(); }
});

test('OAuth public-client metadata cannot be spoofed on foreign hostnames or invoke write methods', async () => {
    const h = await start({bindings:{MAINTENANCE_MODE:'true'}});
    try {
        assert.equal((await h.mf.dispatchFetch(clientId)).status, 200,
            'static public client metadata remains discoverable without a private mailbox');
        assert.equal((await h.mf.dispatchFetch('https://other.example/oauth-client-metadata.json')).status, 404);
        assert.equal((await h.mf.dispatchFetch(clientId,{method:'POST'})).status, 404);
    } finally { await h.close(); }
});
