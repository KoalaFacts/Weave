import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile } from 'node:fs/promises';
import { createHash, webcrypto } from 'node:crypto';
import { createContext, runInContext } from 'node:vm';
import { start } from './harness.ts';
import { agentKey } from './agent-key.ts';

const html = await readFile('src/demo.html', 'utf8');
const script = /<script nonce="WEAVE_NONCE">([\s\S]*?)<\/script>/.exec(html)[1];
const json = (value, status = 200) => new Response(JSON.stringify(value), { status });
const account = { accountId: 'local-demo-agent', displayName: null, description: null };
const accessToken = 'synthetic-access-token-'.padEnd(43, 't');
const tokenGrant = (expiresIn = 900) => json({ access_token: accessToken, token_type: 'DPoP', expires_in: expiresIn });
const base64url = /^[A-Za-z0-9_-]+$/;

function promptText(markup, id) {
    return new RegExp('<textarea id="' + id + '"[^>]*>([\\s\\S]*?)</textarea>').exec(markup)[1]
        .replace(/&(amp|lt);/g, (_, entity) => ({ amp: '&', lt: '<' })[entity]);
}
const decode = jws => { const [header, payload] = jws.split('.').slice(0, 2).map(part => JSON.parse(Buffer.from(part, 'base64url').toString())); return { header, payload }; };
const sha256 = text => createHash('sha256').update(text).digest('base64url');
const thumbprint = x => sha256(JSON.stringify({ crv: 'Ed25519', kty: 'OKP', x }));
function savedKeyFile(accountId = 'saved-agent') {
    const { privateJwk, publicJwk } = agentKey();
    return { text: JSON.stringify({ accountId, key: { kty: 'OKP', crv: 'Ed25519', x: privateJwk.x, d: privateJwk.d } }), x: publicJwk.x };
}

function ui(fetch, { unavailableCrypto = false } = {}) {
    const elements = new Map(), calls = [], downloads = [], blobs = new Map(), timers = [], randomSizes = [], generated = [];
    function element() {
        return { value: '', textContent: '', disabled: false, hidden: false, readOnly: false, children: [], handlers: {}, classList: { toggle() {} },
            addEventListener(name, handler) { this.handlers[name] = handler; },
            append(...children) { this.children.push(...children); },
            replaceChildren(...children) { this.children = children; this.textContent = ''; },
            click() { downloads.push({ href: this.href, filename: this.download, blob: blobs.get(this.href) }); }, remove() {} };
    }
    for (const match of html.matchAll(/<[^>]+\bid="([^"]+)"[^>]*>/g)) {
        const node = element(); node.hidden = /\bhidden\b/.test(match[0]); node.disabled = /\bdisabled\b/.test(match[0]);
        if (match[1].startsWith('agentPrompt')) node.value = promptText(html, match[1]);
        elements.set(match[1], node);
    }
    const get = id => { assert.ok(elements.has(id), 'Missing UI element: ' + id); return elements.get(id); };
    // Real WebCrypto signs; key generation is counted so tests can prove when a new agent key is created.
    const subtle = {
        generateKey: (...args) => { generated.push(args[0].name); return webcrypto.subtle.generateKey(...args); },
        importKey: (...args) => webcrypto.subtle.importKey(...args), exportKey: (...args) => webcrypto.subtle.exportKey(...args),
        sign: (...args) => webcrypto.subtle.sign(...args), digest: (...args) => webcrypto.subtle.digest(...args)
    };
    const context = createContext({
        document: { getElementById: get, createElement: element, body: element() },
        location: { origin: 'http://localhost' },
        fetch: async (path, options) => { calls.push({ path, ...options }); return fetch(path, options); },
        // Deliberately synthetic random bytes; private keys come from WebCrypto, never from this generator.
        crypto: unavailableCrypto ? { getRandomValues() { throw Error('Web Crypto unavailable'); } }
            : { getRandomValues(bytes) { randomSizes.push(bytes.length); return bytes.map((_, i) => i + randomSizes.length); }, subtle },
        TextEncoder, TextDecoder, AbortController, Blob, btoa, atob,
        URL: { createObjectURL(blob) { const url = 'blob:local-test/' + blobs.size; blobs.set(url, blob); return url; }, revokeObjectURL(url) { blobs.delete(url); } },
        setTimeout(fn) { timers.push(fn); },
        console: { log() { assert.fail('UI must not log keys or tokens.'); }, error() { assert.fail('UI must not log keys or tokens.'); } }
    });
    runInContext(script, context);
    return { get, calls, downloads, blobs, generated, flushTimers() { for (const timer of timers.splice(0)) timer(); },
        async click(id) { assert.equal(typeof get(id).handlers.click, 'function', 'Missing action: ' + id); await get(id).handlers.click(); } };
}
const success = path => path === '/v1/accounts' ? json(account) : path === '/v1/token' ? tokenGrant() : json({ items: [], nextCursor: null });
const posts = (app, path) => app.calls.filter(c => c.method === 'POST' && c.path === path);
const registrationJwk = call => decode(call.headers.DPoP).header.jwk;

test('prompt fixture decodes HTML entities once, preserving literal encoded instructions', () => {
    const markup = '<textarea id="agentPromptA">&amp;lt; &amp;amp; &lt;tag> A&amp;B</textarea>';
    assert.equal(promptText(markup, 'agentPromptA'), '&lt; &amp; <tag> A&B');
});

test('opening the demo has no registration, key generation or backup side effects', () => {
    const app = ui(success);
    assert.deepEqual(app.calls, []); assert.deepEqual(app.generated, []); assert.deepEqual(app.downloads, []);
    assert.equal(app.get('credentialBackup').hidden, true);
    assert.equal(app.get('agentKey').value, '');
    assert.doesNotMatch(script, /localStorage|sessionStorage|indexedDB|console\./);
});

test('createAccount_RegistersWithDpopProofAndNoControlHeader', async () => {
    const app = ui(success);
    app.get('displayName').value = 'Private local tester'; app.get('description').value = 'Private description';
    app.get('agentKey').value = 'previously entered recovery value';
    await app.click('createAccount');
    assert.deepEqual(app.generated, ['Ed25519']);
    assert.deepEqual(app.calls.map(c => [c.path, c.method]), [['/v1/accounts', 'POST'], ['/v1/token', 'POST'], ['/v1/addresses?includeRevoked=true', 'GET']]);
    const request = app.calls[0];
    assert.deepEqual(Object.keys(request.headers).sort(), ['Content-Type', 'DPoP']);
    const proof = decode(request.headers.DPoP);
    assert.deepEqual(proof.header, { alg: 'EdDSA', typ: 'dpop+jwt', jwk: { kty: 'OKP', crv: 'Ed25519', x: proof.header.jwk.x } });
    assert.match(proof.header.jwk.x, /^[A-Za-z0-9_-]{43}$/);
    assert.equal(proof.payload.htm, 'POST'); assert.equal(proof.payload.htu, 'http://localhost/v1/accounts');
    assert.ok(Number.isSafeInteger(proof.payload.iat)); assert.match(proof.payload.jti, base64url); assert.equal('ath' in proof.payload, false);
    assert.deepEqual(JSON.parse(request.body), { displayName: 'Private local tester', description: 'Private description' });
    assert.equal(app.get('identity').textContent, 'Account ID: ' + account.accountId);
    assert.equal(app.get('agentKey').value, '');
    assert.equal(app.get('credentialBackup').hidden, false); assert.equal(app.get('createAccount').disabled, true);
    assert.match(app.get('status').textContent, /Back up your agent key/);
    assert.deepEqual(app.downloads, []);
    await app.click('createAccount'); assert.equal(app.calls.length, 3); assert.deepEqual(app.generated, ['Ed25519']);
});

for (const [scenario, field, value, label] of [
    ['multiline description', 'description', 'First line\nSecond line', 'Description'],
    ['control character in name', 'displayName', 'Private\u007fagent', 'Display name'],
    ['C1 control character in description', 'description', 'Private\u0085description', 'Description'],
    ['overlong name', 'displayName', 'n'.repeat(129), 'Display name'],
    ['overlong description', 'description', 'd'.repeat(1025), 'Description']
]) test('invalid profile: ' + scenario + ' stays editable before key generation and can be corrected', async () => {
    const app = ui(success); app.get(field).value = value;
    await app.click('createAccount');
    assert.deepEqual(app.generated, []); assert.deepEqual(app.calls, []);
    assert.equal(app.get('credentialBackup').hidden, true); assert.equal(app.get('createAccount').disabled, false);
    assert.equal(app.get(field).value, value);
    for (const id of ['displayName', 'description']) assert.equal(app.get(id).readOnly, false);
    assert.ok(app.get('status').textContent.startsWith(label + ' must be'));
    assert.match(app.get('status').textContent, /one line.*control characters/);
    app.get(field).value = 'Corrected profile'; await app.click('createAccount');
    assert.deepEqual(app.generated, ['Ed25519']);
    assert.equal(JSON.parse(app.calls[0].body)[field], 'Corrected profile');
    assert.equal(app.get('identity').textContent, 'Account ID: ' + account.accountId);
});

test('downloadKey_ContainsPrivateJwkAndAccountId', async () => {
    const app = ui(success); await app.click('createAccount');
    assert.equal(app.blobs.size, 0); await app.click('downloadCredential');
    assert.equal(app.downloads.length, 1);
    const download = app.downloads[0], exported = JSON.parse(await download.blob.text());
    assert.deepEqual(Object.keys(exported).sort(), ['accountId', 'key']);
    assert.equal(exported.accountId, account.accountId);
    assert.deepEqual(Object.keys(exported.key).sort(), ['crv', 'd', 'kty', 'x']);
    assert.equal(exported.key.kty, 'OKP'); assert.equal(exported.key.crv, 'Ed25519');
    assert.equal(exported.key.x, registrationJwk(app.calls[0]).x);
    assert.equal(typeof exported.key.d, 'string'); assert.match(exported.key.d, /^[A-Za-z0-9_-]{43}$/);
    assert.equal(download.filename, 'weave-demo-agent-key.json'); assert.ok(!download.href.includes(exported.key.d));
    for (const call of app.calls) assert.ok(!JSON.stringify(call).includes(exported.key.d), 'Private key reached the network: ' + call.path);
    assert.match(app.get('status').textContent, /Keep the file private/);
    app.flushTimers(); assert.equal(app.blobs.size, 0);
    await app.click('disconnect'); assert.equal(app.get('credentialBackup').hidden, true);
    await app.click('downloadCredential'); assert.equal(app.downloads.length, 1);
});

for (const mode of ['lost response', 'server failure', 'invalid response', 'missing identity', 'quota denial', 'authentication denial']) {
    test('registration ' + mode + ' retains the exact key and profile for an explicit retry', async () => {
        let attempts = 0;
        const app = ui((path, options) => {
            if (path === '/v1/accounts' && ++attempts === 1) {
                if (mode === 'lost response') throw Error('Connection lost');
                if (mode === 'invalid response') return new Response('invalid JSON');
                if (mode === 'missing identity') return json({});
                return json({ title: 'Request denied' }, mode === 'quota denial' ? 429 : mode === 'authentication denial' ? 401 : 503);
            }
            return success(path, options);
        });
        app.get('displayName').value = 'Original profile'; await app.click('createAccount');
        assert.equal(attempts, 1); assert.equal(app.get('identity').textContent, 'No account connected.');
        assert.equal(app.get('credentialBackup').hidden, false); assert.equal(app.get('createAccount').disabled, false);
        assert.match(app.get('createAccount').textContent, /Retry/);
        assert.match(app.get('status').textContent, mode.includes('denial') ? /rejected/ : /unknown/);
        await app.click('downloadCredential');
        assert.equal(JSON.parse(await app.downloads[0].blob.text()).key.x, registrationJwk(app.calls[0]).x);
        app.get('displayName').value = 'Edited while retrying'; await app.click('createAccount');
        const registrations = posts(app, '/v1/accounts');
        assert.equal(registrations.length, 2); assert.equal(registrations[0].body, registrations[1].body);
        assert.deepEqual(registrationJwk(registrations[0]), registrationJwk(registrations[1])); assert.deepEqual(app.generated, ['Ed25519']);
        assert.equal(app.get('identity').textContent, 'Account ID: ' + account.accountId);
    });
}

test('confirmed registration with a failed address refresh stays connected and never registers again', async () => {
    const app = ui(path => path === '/v1/accounts' ? json(account) : path === '/v1/token' ? tokenGrant() : json({ title: 'Storage unavailable' }, 503));
    await app.click('createAccount');
    assert.equal(app.get('identity').textContent, 'Account ID: ' + account.accountId);
    assert.match(app.get('status').textContent, /registered.*refresh addresses/i);
    assert.match(app.get('status').textContent, /Back up your agent key/);
    await app.click('createAccount'); assert.equal(posts(app, '/v1/accounts').length, 1);
});

test('overlapping registration clicks do not dispatch twice and clearing ignores a late response', async () => {
    let resolveRegistration;
    const app = ui(() => new Promise(resolve => { resolveRegistration = resolve; }));
    const pending = app.click('createAccount'); await app.click('createAccount');
    while (app.calls.length === 0) await new Promise(resolve => setTimeout(resolve, 1));
    assert.equal(app.calls.length, 1); assert.deepEqual(app.generated, ['Ed25519']);
    await app.click('disconnect'); resolveRegistration(json(account)); await pending;
    assert.equal(app.get('identity').textContent, 'No account connected.'); assert.equal(app.get('credentialBackup').hidden, true);
    assert.equal(app.calls.length, 1); assert.equal(app.get('createAccount').disabled, false);
    assert.equal(app.get('workspace').hidden, true); assert.equal(app.get('accountStart').hidden, false);
});

test('clearing while a key is being generated discards it without registration', async () => {
    const app = ui(success);
    const pending = app.click('createAccount'); await app.click('disconnect'); await pending;
    assert.deepEqual(app.calls, []); assert.equal(app.get('credentialBackup').hidden, true);
    assert.equal(app.get('identity').textContent, 'No account connected.');
});

test('unavailableCrypto_ShowsKeyGenerationUnavailable', async () => {
    const app = ui(success, { unavailableCrypto: true }); await app.click('createAccount');
    assert.deepEqual(app.calls, []); assert.equal(app.get('credentialBackup').hidden, true);
    assert.match(app.get('status').textContent, /Secure key generation.*unavailable/);
    assert.equal(app.get('createAccount').disabled, false);
});

test('resume_InvalidKeyFile_ShowsErrorWithoutRequest', async () => {
    const valid = JSON.parse(savedKeyFile().text);
    const app = ui(success);
    for (const value of ['', 'not json', '[]', '{}', JSON.stringify({ accountId: 'a' }),
        JSON.stringify({ ...valid, key: { ...valid.key, d: undefined } }), JSON.stringify({ ...valid, key: { ...valid.key, d: 'short' } }),
        JSON.stringify({ ...valid, key: { ...valid.key, kty: 'EC' } }), JSON.stringify({ ...valid, key: { ...valid.key, crv: 'X25519' } }),
        JSON.stringify({ ...valid, accountId: 7 }), JSON.stringify({ ...valid, key: { ...valid.key, x: valid.key.x.slice(1) } })]) {
        app.get('agentKey').value = value; await app.click('register');
        assert.match(app.get('status').textContent, /saved agent key file/i, value);
        assert.deepEqual(app.calls, [], value); assert.equal(app.get('credentialBackup').hidden, true);
        assert.doesNotMatch(app.get('identity').textContent, /Account ID/);
    }
    assert.deepEqual(app.generated, []);
});

test('separate recovery signs with the saved key without generating one or retrying an auth denial anonymously', async () => {
    const saved = savedKeyFile();
    const app = ui(() => json({ title: 'Unauthenticated' }, 401));
    app.get('agentKey').value = saved.text; await app.click('register');
    assert.deepEqual(app.generated, []); assert.equal(app.calls.length, 1);
    assert.equal(registrationJwk(app.calls[0]).x, saved.x);
    assert.equal(app.get('identity').textContent, 'No account connected.');
    assert.match(app.get('status').textContent, /rejected.*401/);
});

test('privateCalls_CarryDpopTokenAndProof', async () => {
    let expiresIn = 900;
    const app = ui(async (path, options) => {
        if (path === '/v1/token') return tokenGrant(expiresIn);
        if (path === '/v1/inbox/events') return new Response('');
        return success(path, options);
    });
    await app.click('createAccount');
    const x = registrationJwk(app.calls[0]).x;
    const grant = app.calls[1];
    assert.equal(grant.headers['Content-Type'], 'application/x-www-form-urlencoded');
    assert.equal('Authorization' in grant.headers, false);
    const form = new URLSearchParams(grant.body);
    assert.deepEqual([...form.keys()], ['grant_type', 'client_assertion_type', 'client_assertion']);
    assert.equal(form.get('grant_type'), 'client_credentials');
    assert.equal(form.get('client_assertion_type'), 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer');
    const assertion = decode(form.get('client_assertion'));
    assert.deepEqual(assertion.header, { alg: 'EdDSA', kid: thumbprint(x) });
    assert.equal(assertion.payload.iss, account.accountId); assert.equal(assertion.payload.sub, account.accountId);
    assert.equal(assertion.payload.aud, 'http://localhost/v1/token');
    assert.equal(assertion.payload.exp, assertion.payload.iat + 120);
    assert.equal(Buffer.from(assertion.payload.jti, 'base64url').length, 16); assert.match(assertion.payload.jti, base64url);
    const grantProof = decode(grant.headers.DPoP);
    assert.equal(grantProof.payload.htu, 'http://localhost/v1/token'); assert.equal(grantProof.payload.htm, 'POST');
    assert.equal('ath' in grantProof.payload, false); assert.equal(grantProof.header.jwk.x, x);

    await app.click('loadAddresses'); await app.click('stream');
    const privateCalls = app.calls.filter(c => !['/v1/accounts', '/v1/token'].includes(c.path));
    assert.deepEqual(privateCalls.map(c => c.path), ['/v1/addresses?includeRevoked=true', '/v1/addresses?includeRevoked=true', '/v1/inbox/events']);
    for (const call of privateCalls) {
        assert.equal(call.headers.Authorization, 'DPoP ' + accessToken);
        const proof = decode(call.headers.DPoP);
        assert.equal(proof.header.typ, 'dpop+jwt'); assert.equal(proof.header.jwk.x, x);
        assert.equal(proof.payload.htm, 'GET'); assert.equal(proof.payload.ath, sha256(accessToken));
    }
    assert.equal(decode(privateCalls[0].headers.DPoP).payload.htu, 'http://localhost/v1/addresses');
    assert.equal(decode(privateCalls[2].headers.DPoP).payload.htu, 'http://localhost/v1/inbox/events');
    assert.equal(posts(app, '/v1/token').length, 1, 'A cached token is reused while more than 60 s remain.');

    // A token with less than 60 s left is renewed before the next private call.
    await app.click('disconnect'); expiresIn = 59; await app.click('createAccount');
    await app.click('loadAddresses');
    assert.equal(posts(app, '/v1/token').length, 3);
});

test('resumeFromKeyFile_SameAgent', async () => {
    const h = await start();
    try {
        const app = ui((path, options) => h.mf.dispatchFetch('http://localhost' + path, options));
        await app.click('createAccount');
        const identity = app.get('identity').textContent;
        assert.match(identity, /^Account ID: /);
        await app.click('downloadCredential');
        const backup = await app.downloads[0].blob.text();
        await app.click('disconnect'); assert.equal(app.get('identity').textContent, 'No account connected.');
        app.get('agentKey').value = backup; await app.click('register');
        assert.equal(app.get('identity').textContent, identity);
        assert.match(app.get('status').textContent, /Account connected/);
        assert.deepEqual(app.generated, ['Ed25519']);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts').first('n'), 1);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM agent_keys').first('n'), 1);
    } finally { await h.close(); }
});

test('real Worker commits a lost registration once, resumes its saved key, and enforces quota for the next agent', async () => {
    const h = await start({ bindings: { REGISTRATION_MAX_ACCOUNTS: '1' } });
    let loseResponse = true;
    try {
        const app = ui(async (path, options) => {
            const response = await h.mf.dispatchFetch('http://localhost' + path, options);
            if (path === '/v1/accounts' && loseResponse) {
                loseResponse = false; assert.equal(response.status, 200); throw Error('Response lost after commit');
            }
            return response;
        });
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts').first('n'), 0);
        app.get('description').value = 'First line\nSecond line'; await app.click('createAccount');
        assert.deepEqual(app.calls, []); assert.deepEqual(app.generated, []);
        assert.equal(app.get('description').readOnly, false);
        app.get('description').value = 'First line Second line';
        await app.click('createAccount');
        assert.match(app.get('status').textContent, /outcome unknown/);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts').first('n'), 1);
        assert.equal(await h.db.prepare('SELECT description FROM accounts').first('description'), 'First line Second line');
        await app.click('createAccount');
        const identity = app.get('identity').textContent;
        assert.match(identity, /^Account ID: /);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts').first('n'), 1);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM account_addresses').first('n'), 0);
        await app.click('downloadCredential');
        const backup = await app.downloads[0].blob.text();
        await app.click('disconnect'); app.get('agentKey').value = backup; await app.click('register');
        assert.equal(app.get('identity').textContent, identity);
        await app.click('disconnect'); await app.click('createAccount');
        assert.match(app.get('status').textContent, /rejected \(429\)/);
        assert.equal(app.get('identity').textContent, 'No account connected.');
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts').first('n'), 1);
    } finally { await h.close(); }
});

test('new account shows backup before scenario controls and continue never downloads or registers again', async () => {
    const app = ui(success);
    assert.equal(app.get('workspace').hidden, true);
    assert.equal(app.get('accountStart').hidden, false);
    await app.click('continueToContacts');
    assert.equal(app.get('workspace').hidden, true);
    await app.click('createAccount');
    assert.equal(app.get('workspace').hidden, true);
    assert.equal(app.get('accountStart').hidden, true);
    assert.equal(app.get('continueToContacts').hidden, false);
    await app.click('continueToContacts');
    assert.equal(app.get('workspace').hidden, false);
    assert.equal(app.get('continueToContacts').hidden, true);
    assert.deepEqual(app.downloads, []);
    assert.equal(posts(app, '/v1/accounts').length, 1);
    await app.click('disconnect');
    assert.equal(app.get('workspace').hidden, true);
    assert.equal(app.get('accountStart').hidden, false);
});

test('unknown registration keeps retry and backup visible but cannot expose connected scenarios', async () => {
    const app = ui(() => { throw Error('Response lost'); });
    await app.click('createAccount');
    assert.equal(app.get('accountStart').hidden, false);
    assert.equal(app.get('credentialBackup').hidden, false);
    assert.equal(app.get('continueToContacts').hidden, true);
    await app.click('continueToContacts');
    assert.equal(app.get('workspace').hidden, true);
    assert.equal(app.calls.length, 1);
});

test('restoring a saved account opens scenarios without generating another key', async () => {
    const app = ui(success);
    app.get('agentKey').value = savedKeyFile(account.accountId).text;
    await app.click('register');
    assert.equal(app.get('workspace').hidden, false);
    assert.equal(app.get('accountStart').hidden, true);
    assert.equal(app.get('continueToContacts').hidden, true);
    assert.deepEqual(app.generated, []);
    assert.deepEqual(app.downloads, []);
});

test('a saved key that registers as a different account says demo data may have been cleared', async () => {
    const app = ui(success);
    app.get('agentKey').value = savedKeyFile('cleared-agent').text;
    await app.click('register');
    assert.equal(app.get('identity').textContent, 'Account ID: ' + account.accountId);
    assert.match(app.get('status').textContent, /new account; demo data may have been cleared/);
});

test('agent instructions use this deployment origin without account or key side effects', () => {
    const app = ui(success);
    for (const suffix of ['A', 'B']) {
        const prompt = app.get('agentPrompt' + suffix).value;
        const origins = [...prompt.matchAll(/https?:\/\/\S+/g)].map(([url]) => new URL(url.replace(/\.$/, '')).origin);
        assert.deepEqual(origins, ['http://localhost']);
    }
    assert.deepEqual(app.calls, []); assert.deepEqual(app.generated, []); assert.deepEqual(app.downloads, []);
});
