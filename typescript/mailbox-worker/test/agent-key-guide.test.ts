import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { start } from './harness.ts';
import { accessTokenSeconds, assertionMaxSeconds } from '../src/agent-tokens.ts';
import { keyCoolingTicks, maxLiveKeysPerAgent } from '../src/agent-keys.ts';
import { dpopWindowSeconds } from '../src/dpop.ts';

const guide = readFileSync(new URL('../src/agent-start.md', import.meta.url), 'utf8');
const section = (heading: string, next: string) => guide.split(heading)[1]?.split(next)[0] ?? '';

test('guide teaches only agent keys, never the removed bearer credential', () => {
    assert.doesNotMatch(guide, /control/i);
    assert.doesNotMatch(guide, /X-Weave-|WEAVE_ACCOUNT|WEAVE_MAILBOX/);
});
test('existing-agent diagnosis uses direct reads without restore, registration or ACK', () => {
    const diagnosis = section('### Read-only diagnosis of an existing agent', '## 2.');
    assert.match(diagnosis, /GET \/v1\/contacts\/requests/);
    assert.match(diagnosis, /GET \/v1\/inbox/);
    assert.match(diagnosis, /Do not click Restore account or call `POST \/v1\/accounts` as a prerequisite/);
    assert.match(diagnosis, /Reading does not accept a contact or ACK a message/);
    assert.match(diagnosis, /401 or 404/);
});
test('key-first guide gates registration on real key storage and signing capabilities', () => {
    const steps = ['### 2.1 Verify key storage and signing', '### 2.2 Generate and save the key before registering', '### 2.3 Obtain and renew access tokens',
        '### 2.4 Verify recovery without silently replacing identity', '### 2.5 Back up, rotate and revoke keys'];
    let previous = -1;
    for (const step of steps) {
        const position = guide.indexOf(step);
        assert.ok(position > previous, 'Missing or out-of-order step: ' + step); previous = position;
    }
    const gate = section(steps[0], steps[1]);
    for (const term of ['secure_key_storage', 'secure_signing', 'needs_owner_action', 'required confirmations', 'not published yet']) assert.ok(gate.includes(term), term);
    assert.match(gate, /downloaded agent key file is a backup, not a vault/i);
    assert.match(gate, /Do not generate a key or register/);
    assert.match(guide, /Never put the private key in chat, logs, repositories, ordinary notes, plaintext files/);
});
test('registration in the guide matches the documented DPoP-proof-only operation', () => {
    const contract = JSON.parse(readFileSync(new URL('../src/openapi.json', import.meta.url), 'utf8'));
    assert.deepEqual(contract.paths['/v1/accounts'].post.security, [{DPoPProof: []}]);
    assert.deepEqual(contract.components.schemas.accountSubmission.required ?? [], []);
    const registration = section('### 2.2 Generate and save the key before registering', '### 2.3');
    assert.match(registration, /POST \/v1\/accounts/); assert.match(registration, /JSON body `\{\}`/);
    assert.match(registration, /no `Authorization` header/);
    for (const term of ['dpop+jwt', '"kty":"OKP"', '"crv":"Ed25519"', 'kid', 'RFC 7638']) assert.ok(registration.includes(term), term);
});
test('token section pins the implemented lifetimes, skew and renewal rule', () => {
    const tokens = section('### 2.3 Obtain and renew access tokens', '### 2.4');
    for (const term of ['POST /v1/token', 'private_key_jwt', 'application/x-www-form-urlencoded', 'grant_type=client_credentials',
        'urn:ietf:params:oauth:client-assertion-type:jwt-bearer', 'Authorization: DPoP', '`ath`', 'invalid_client', 'WWW-Authenticate: DPoP algs="EdDSA"'])
        assert.ok(tokens.includes(term), term);
    assert.ok(tokens.includes(`"expires_in":${accessTokenSeconds}`), 'expires_in');
    assert.ok(tokens.includes(`${accessTokenSeconds / 60} minutes`), 'token lifetime');
    assert.ok(tokens.includes(`now + ${assertionMaxSeconds}`), 'assertion lifetime');
    assert.ok(tokens.includes(`±${dpopWindowSeconds} s`), 'clock skew');
    assert.match(tokens, /On a 401.*obtain a new token/s);
    assert.match(tokens, /Do not stop, register again or generate a new key/);
    assert.match(tokens, /Every DPoP proof in this section.*same JWS header as registration.*"typ":"dpop\+jwt".*"jwk":<public JWK>/s);
    assert.match(tokens, /401 from `POST \/v1\/accounts` is not an expired token/);
});
test('key management states cooling, the 100-key limit and the lost-key consequence', () => {
    const keys = section('### 2.5 Back up, rotate and revoke keys', '### 2.6');
    for (const term of ['GET /v1/keys', 'POST /v1/keys', 'DELETE /v1/keys/{kid}', 'weave-key-proof+jwt', 'weave-key-endorsement+jwt', 'newKid', 'coolingUntil',
        'last-live-key', 'owned-agent', 'key-in-use']) assert.ok(keys.includes(term), term);
    assert.equal(keyCoolingTicks, 24n * 3600n * 10000000n);
    assert.ok(keys.includes('24 hours'), 'cooling period');
    assert.ok(keys.includes(`${maxLiveKeysPerAgent} live keys`), 'key limit');
    assert.match(keys, /cooling key may revoke only itself/);
    assert.match(keys, /no settled key can be revoked/);
    assert.match(keys, /cannot endorse/);
    assert.match(keys, /loses every key is lost/);
    assert.match(keys, /event stream opened before the revocation ends within about 20 seconds/);
});
test('guide distinguishes durable saving, registration and restoration evidence', () => {
    for (const term of ['registrationStatus', 'keyPersistenceStatus', 'recoveryVerificationStatus', 'service origin', 'accountId', 'fresh session', 'different Account ID', 'Do not overwrite']) assert.ok(guide.includes(term), term);
    assert.match(guide, /invalid_client.*not proof.*demo clearing/);
    assert.match(guide, /do not automatically call registration/i);
    assert.match(guide, /same saved key/);
});
test('anonymous HTTP and page prompts share the key-first guide without account operations', async () => {
    const h = await start();
    try {
        assert.equal(await (await h.request('/agent-start.md')).text(), guide);
        const html = await (await h.request('/')).text();
        for (const role of ['A', 'B']) {
            const prompt = new RegExp('<textarea id="agentPrompt' + role + '"[^>]*>([\\s\\S]*?)</textarea>').exec(html)[1];
            assert.match(prompt, /key-first/); assert.match(prompt, /secure key storage and signing/);
            assert.match(prompt, /agent-start\.md/);
            assert.doesNotMatch(prompt, /vault-first|secret injection/);
        }
        assert.match(html, /downloaded agent key file is a backup, not a vault/i);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM accounts").first('n'), 0);
    } finally { await h.close(); }
});
test('agent-start.md is served as uncached non-sniffable markdown', async () => {
    const h = await start();
    try {
        const response = await h.request('/agent-start.md');
        assert.equal(response.status, 200);
        assert.match(response.headers.get('content-type') ?? '', /^text\/markdown/);
        assert.equal(response.headers.get('cache-control'), 'no-store');
        assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
    } finally { await h.close(); }
});
test('home page has two nonce-bound scripts, no leftover placeholder and creates no account', async () => {
    const h = await start();
    try {
        const home = await h.request('/');
        const html = await home.text(), csp = home.headers.get('content-security-policy') ?? '';
        const scripts = [...html.matchAll(/<script nonce="([^"]+)">/g)];
        assert.equal(scripts.length, 2);
        for (const [, nonce] of scripts) assert.ok(csp.includes("'nonce-" + nonce + "'"));
        assert.ok(!html.includes('WEAVE_NONCE'));
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts').first('n'), 0);
    } finally { await h.close(); }
});
