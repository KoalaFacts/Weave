import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { start } from './harness.ts';
import { agentKey, clientAssertion, dpopProof, nowSeconds, type TestKey } from './agent-key.ts';
import { issueAgentToken } from '../src/agent-tokens.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Credential = {accountId: string; key: TestKey};
type AssertionOverrides = Partial<Parameters<typeof clientAssertion>[1]>;
type ProofOverrides = Partial<Parameters<typeof dpopProof>[1]> & {key?: TestKey};

const origin = 'http://localhost';
const tokenUrl = origin + '/v1/token';
const assertionType = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer';

async function register(h: Harness): Promise<Credential> {
    const key = agentKey(), url = origin + '/v1/accounts';
    const response = await h.mf.dispatchFetch(url, {method: 'POST', headers: {'Content-Type': 'application/json', DPoP: dpopProof(key, {method: 'POST', url})}, body: '{}'});
    assert.equal(response.status, 200, await response.clone().text());
    return {accountId: (await response.json() as {accountId: string}).accountId, key};
}
function post(fields: [string, string][], proof: string | null, contentType = 'application/x-www-form-urlencoded', body?: string): RequestInit {
    return {method: 'POST', headers: {'Content-Type': contentType, ...(proof === null ? {} : {DPoP: proof})}, body: body ?? new URLSearchParams(fields).toString()};
}
function grant(assertion: string): [string, string][] {
    return [['grant_type', 'client_credentials'], ['client_assertion_type', assertionType], ['client_assertion', assertion]];
}
function token(h: Harness, cred: Credential, assertion: AssertionOverrides = {}, proof: ProofOverrides = {}): Promise<Response> {
    const signed = clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl, ...assertion});
    return h.mf.dispatchFetch(tokenUrl, post(grant(signed), dpopProof(proof.key ?? cred.key, {method: 'POST', url: tokenUrl, ...proof}))) as unknown as Promise<Response>;
}
async function issue(h: Harness, cred: Credential): Promise<string> {
    const response = await token(h, cred);
    assert.equal(response.status, 200, await response.clone().text());
    return (await response.json() as {access_token: string}).access_token;
}
function call(h: Harness, cred: Credential, accessToken: string, path: string, method = 'GET', body?: unknown, proof: ProofOverrides = {}, scheme = 'DPoP'): Promise<Response> {
    const url = origin + path;
    return h.mf.dispatchFetch(url, {method, headers: {Authorization: `${scheme} ${accessToken}`, DPoP: dpopProof(proof.key ?? cred.key, {method, url, accessToken, ...proof}),
        ...(body === undefined ? {} : {'Content-Type': 'application/json'})}, ...(body === undefined ? {} : {body: JSON.stringify(body)})}) as unknown as Promise<Response>;
}
async function refusal(response: Response, status: number, error: string): Promise<void> {
    const text = await response.text();
    assert.equal(response.status, status, text);
    assert.deepEqual(JSON.parse(text), {error});
    assert.equal(response.headers.get('Cache-Control'), 'no-store');
}
const count = async (h: Harness, table: string) => Number(await h.db.prepare(`SELECT count(*) n FROM ${table}`).first('n'));
const nowTicks = () => (BigInt(Date.now()) * 10000n).toString();
// Exact second edges need the request to finish inside the second in which the test computed "now".
async function freshSecond(): Promise<void> { while (Date.now() % 1000 > 100) await delay(5); }

test('token_ValidAssertionAndProof_ReturnsDpopToken', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const response = await token(h, cred);
        assert.equal(response.status, 200, await response.clone().text());
        assert.equal(response.headers.get('Cache-Control'), 'no-store');
        const result = await response.json() as {access_token: string; token_type: string; expires_in: number};
        assert.deepEqual(Object.keys(result).sort(), ['access_token', 'expires_in', 'token_type']);
        assert.equal(result.token_type, 'DPoP');
        assert.equal(result.expires_in, 900);
        assert.match(result.access_token, /^[A-Za-z0-9_-]{43}$/);
        const row = await h.db.prepare('SELECT token_hash,account_id,kid,CAST(expires_at AS TEXT) expires_at,CAST(created_at AS TEXT) created_at FROM agent_access_tokens').first<any>();
        assert.notEqual(row.token_hash, result.access_token);
        assert.equal(row.token_hash, createHash('sha256').update(result.access_token).digest('hex'));
        assert.equal(row.account_id, cred.accountId);
        assert.equal(row.kid, cred.key.kid);
        assert.equal(BigInt(row.expires_at) - BigInt(row.created_at), 9000000000n);
        assert.notEqual(await h.db.prepare('SELECT last_used_at FROM agent_keys WHERE kid=?').bind(cred.key.kid).first('last_used_at'), null);
    } finally { await h.close(); }
});

test('token_ThenPrivateRequest_Authenticates', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const response = await call(h, cred, await issue(h, cred), '/v1/addresses');
        assert.equal(response.status, 200, await response.clone().text());
        assert.deepEqual(await response.json(), {items: [], nextCursor: null});
    } finally { await h.close(); }
});

test('token_ReplayedAssertion_401InvalidClient', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const jti = 'replayed-assertion-0001';
        assert.equal((await token(h, cred, {jti})).status, 200);
        await refusal(await token(h, cred, {jti}), 401, 'invalid_client');
        assert.equal(await count(h, 'agent_access_tokens'), 1);
    } finally { await h.close(); }
});

test('token_ConcurrentSameAssertion_ExactlyOneToken', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const assertion = clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl});
        const send = () => h.mf.dispatchFetch(tokenUrl, post(grant(assertion), dpopProof(cred.key, {method: 'POST', url: tokenUrl})));
        const responses = await Promise.all([send(), send()]);
        assert.deepEqual(responses.map(r => r.status).sort(), [200, 401]);
        assert.equal(await count(h, 'agent_access_tokens'), 1);
        assert.equal(await count(h, 'agent_assertions'), 1);
    } finally { await h.close(); }
});

test('token_AssertionStoreUnavailable_503AndNoToken', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        await h.db.prepare('DROP TABLE agent_assertions').run();
        await refusal(await token(h, cred), 503, 'temporarily_unavailable');
        assert.equal(await count(h, 'agent_access_tokens'), 0);
        assert.equal(await h.db.prepare('SELECT last_used_at FROM agent_keys WHERE kid=?').bind(cred.key.kid).first('last_used_at'), null);
    } finally { await h.close(); }
});

test('token_Audience_ExactOnly', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        await refusal(await token(h, cred, {audience: tokenUrl + '/'}), 401, 'invalid_client');
        await refusal(await token(h, cred, {audience: origin + '/v1/accounts'}), 401, 'invalid_client');
        assert.equal((await token(h, cred, {claims: {aud: [tokenUrl]}})).status, 200);
        await refusal(await token(h, cred, {claims: {aud: [tokenUrl, 'https://other.example/v1/token']}}), 401, 'invalid_client');
    } finally { await h.close(); }
});

test('token_AssertionTiming', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        await freshSecond();
        let now = nowSeconds();
        await refusal(await token(h, cred, {exp: now}), 401, 'invalid_client');
        await freshSecond();
        now = nowSeconds();
        await refusal(await token(h, cred, {exp: now + 301}), 401, 'invalid_client');
        await refusal(await token(h, cred, {iat: nowSeconds() + 90}), 401, 'invalid_client');
        await refusal(await token(h, cred, {iat: nowSeconds() - 400}), 401, 'invalid_client');
        await refusal(await token(h, cred, {claims: {nbf: nowSeconds() + 90}}), 401, 'invalid_client');
        await refusal(await token(h, cred, {claims: {exp: String(nowSeconds() + 60)}}), 401, 'invalid_client');
        await refusal(await token(h, cred, {jti: 'short'}), 401, 'invalid_client');
        assert.equal((await token(h, cred, {exp: nowSeconds() + 290})).status, 200);
        assert.equal(await count(h, 'agent_access_tokens'), 1);
    } finally { await h.close(); }
});

test('token_IssNotSubOrUnknownKidOrRevokedKey_401', async () => {
    const h = await start();
    try {
        const cred = await register(h), other = await register(h);
        await refusal(await token(h, cred, {claims: {iss: other.accountId}}), 401, 'invalid_client');
        await refusal(await token(h, cred, {accountId: other.accountId}), 401, 'invalid_client');
        const unknown = agentKey();
        await refusal(await token(h, {accountId: cred.accountId, key: unknown}), 401, 'invalid_client');
        // Signed by another key while naming this key's kid.
        await refusal(await token(h, {accountId: cred.accountId, key: other.key}, {header: {kid: cred.key.kid}}), 401, 'invalid_client');
        await h.db.prepare('UPDATE agent_keys SET revoked_at=created_at WHERE kid=?').bind(cred.key.kid).run();
        await refusal(await token(h, cred), 401, 'invalid_client');
        assert.equal(await count(h, 'agent_access_tokens'), 0);
    } finally { await h.close(); }
});

test('token_ClientIdMustNameTheAgent', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const signed = () => grant(clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl}));
        const proof = () => dpopProof(cred.key, {method: 'POST', url: tokenUrl});
        await refusal(await h.mf.dispatchFetch(tokenUrl, post([...signed(), ['client_id', 'someone-else']], proof())), 401, 'invalid_client');
        assert.equal((await h.mf.dispatchFetch(tokenUrl, post([...signed(), ['client_id', cred.accountId]], proof()))).status, 200);
    } finally { await h.close(); }
});

test('token_DpopKeyDiffersFromAssertionKey_400InvalidDpopProof', async () => {
    const h = await start();
    try {
        const cred = await register(h), second = await register(h);
        await refusal(await token(h, cred, {}, {key: second.key}), 400, 'invalid_dpop_proof');
        assert.equal(await count(h, 'agent_access_tokens'), 0);
        assert.equal(await count(h, 'agent_assertions'), 0);
    } finally { await h.close(); }
});

test('token_ProofJwkWithD_400', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const response = await token(h, cred, {}, {header: {jwk: {...cred.key.publicJwk, d: cred.key.privateJwk.d}}});
        const text = await response.clone().text();
        await refusal(response, 400, 'invalid_dpop_proof');
        assert.equal(text.includes(cred.key.privateJwk.d!), false);
        await refusal(await token(h, cred, {}, {accessToken: 'x'.repeat(43)}), 400, 'invalid_dpop_proof');
        const missing = await h.mf.dispatchFetch(tokenUrl, post(grant(clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl})), null));
        await refusal(missing, 400, 'invalid_dpop_proof');
        assert.equal(await count(h, 'agent_access_tokens'), 0);
    } finally { await h.close(); }
});

test('token_BadForm_400', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const assertion = () => clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl});
        const proof = () => dpopProof(cred.key, {method: 'POST', url: tokenUrl});
        const send = (init: RequestInit) => h.mf.dispatchFetch(tokenUrl, init);
        await refusal(await send(post([], proof(), 'application/json', JSON.stringify(Object.fromEntries(grant(assertion()))))), 400, 'invalid_request');
        await refusal(await send(post([...grant(assertion()), ['client_assertion', assertion()]], proof())), 400, 'invalid_request');
        await refusal(await send(post([...grant(assertion()), ['scope', 'weave']], proof())), 400, 'invalid_request');
        await refusal(await send(post(grant(assertion()).filter(([name]) => name !== 'client_assertion'), proof())), 400, 'invalid_request');
        await refusal(await send(post([...grant(assertion()), ['padding', 'x'.repeat(8192)]], proof())), 400, 'invalid_request');
        await refusal(await send(post([['grant_type', 'password'], ...grant(assertion()).slice(1)], proof())), 400, 'unsupported_grant_type');
        await refusal(await send(post([grant(assertion())[0], ['client_assertion_type', 'urn:other'], grant(assertion())[2]], proof())), 400, 'invalid_request');
        assert.equal(await count(h, 'agent_access_tokens'), 0);
    } finally { await h.close(); }
});

test('token_MaintenanceAndRecoveryGates', async () => {
    const persist = await mkdtemp(join(tmpdir(), 'weave-d1-'));
    let h = await start({persist});
    try {
        const cred = await register(h);
        const accessToken = await issue(h, cred);
        await h.close();
        h = await start({persist, migrate: false, bindings: {RECOVERY_READ_ONLY: 'true'}});
        assert.equal((await token(h, cred)).status, 503);
        const read = await call(h, cred, accessToken, '/v1/addresses');
        assert.equal(read.status, 200, await read.clone().text());
        assert.equal((await call(h, cred, accessToken, '/v1/addresses', 'POST', {})).status, 503);
        await h.close();
        h = await start({persist, migrate: false, bindings: {MAINTENANCE_MODE: 'true'}});
        assert.equal((await token(h, cred)).status, 503);
        assert.equal((await call(h, cred, accessToken, '/v1/addresses')).status, 503);
        assert.equal(await count(h, 'agent_access_tokens'), 1);
    } finally { await h.close(); await rm(persist, {recursive: true, force: true}); }
});

test('resource_KeyRevokedAfterIssue_NextRequest401', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const accessToken = await issue(h, cred);
        assert.equal((await call(h, cred, accessToken, '/v1/addresses')).status, 200);
        await h.db.prepare('UPDATE agent_keys SET revoked_at=created_at WHERE kid=?').bind(cred.key.kid).run();
        assert.equal((await call(h, cred, accessToken, '/v1/addresses')).status, 401);
    } finally { await h.close(); }
});

test('resource_ExpiredToken_401', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const accessToken = await issue(h, cred);
        await h.db.prepare('UPDATE agent_access_tokens SET expires_at=CAST(? AS INTEGER)').bind(nowTicks()).run();
        assert.equal((await call(h, cred, accessToken, '/v1/addresses')).status, 401);
    } finally { await h.close(); }
});

test('resource_BearerSchemeOrMissingProof_401', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const accessToken = await issue(h, cred);
        assert.equal((await call(h, cred, accessToken, '/v1/addresses', 'GET', undefined, {}, 'Bearer')).status, 401);
        const missing = await h.mf.dispatchFetch(origin + '/v1/addresses', {headers: {Authorization: 'DPoP ' + accessToken}});
        assert.equal(missing.status, 401);
        assert.equal((await call(h, cred, accessToken + 'x', '/v1/addresses')).status, 401);
        assert.equal((await call(h, cred, 'A'.repeat(43), '/v1/addresses')).status, 401);
    } finally { await h.close(); }
});

test('resource_ProofWithoutAthOrForOtherUrl_401', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const accessToken = await issue(h, cred);
        const url = origin + '/v1/addresses';
        const noAth = await h.mf.dispatchFetch(url, {headers: {Authorization: 'DPoP ' + accessToken, DPoP: dpopProof(cred.key, {method: 'GET', url})}});
        assert.equal(noAth.status, 401);
        assert.equal((await call(h, cred, accessToken, '/v1/addresses', 'GET', undefined, {url: origin + '/v1/contacts'})).status, 401);
        assert.equal((await call(h, cred, accessToken, '/v1/addresses', 'GET', undefined, {method: 'POST'})).status, 401);
    } finally { await h.close(); }
});

test('resource_ProofIatOutsideWindow_401', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const accessToken = await issue(h, cred);
        assert.equal((await call(h, cred, accessToken, '/v1/addresses', 'GET', undefined, {iat: nowSeconds() - 65})).status, 401);
        assert.equal((await call(h, cred, accessToken, '/v1/addresses', 'GET', undefined, {iat: nowSeconds() + 65})).status, 401);
    } finally { await h.close(); }
});

test('resource_TokenWithAnotherKeysProof_401', async () => {
    const h = await start();
    try {
        const cred = await register(h), other = await register(h);
        const accessToken = await issue(h, cred);
        assert.equal((await call(h, cred, accessToken, '/v1/addresses', 'GET', undefined, {key: other.key})).status, 401);
    } finally { await h.close(); }
});

test('resource_ProofReusedWithinWindow_Accepted', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const accessToken = await issue(h, cred);
        const url = origin + '/v1/addresses';
        const init = {headers: {Authorization: 'DPoP ' + accessToken, DPoP: dpopProof(cred.key, {method: 'GET', url, accessToken})}};
        assert.equal((await h.mf.dispatchFetch(url, init)).status, 200);
        assert.equal((await h.mf.dispatchFetch(url, init)).status, 200);
    } finally { await h.close(); }
});

test('resource_AgentTokenWithControl_401', async () => {
    const h = await start();
    try {
        const cred = await register(h), control = await h.register();
        const accessToken = await issue(h, cred);
        const url = origin + '/v1/addresses';
        const response = await h.mf.dispatchFetch(url, {headers: {Authorization: 'DPoP ' + accessToken, DPoP: dpopProof(cred.key, {method: 'GET', url, accessToken}),
            'X-Weave-Account-Control': control.control}});
        assert.equal(response.status, 401);
    } finally { await h.close(); }
});

test('resource_401_CarriesDpopChallenge', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const response = await call(h, cred, 'A'.repeat(43), '/v1/addresses');
        assert.equal(response.status, 401);
        const challenge = response.headers.get('WWW-Authenticate') ?? '';
        assert.match(challenge, /DPoP/);
        assert.match(challenge, /algs="EdDSA"/);
    } finally { await h.close(); }
});

// Direct calls with an injected clock: each read of the clock returns the next value (the last one repeats).
const second = 10000000n;
const clockOf = (...seconds: number[]) => { let i = 0; return () => BigInt(seconds[Math.min(i++, seconds.length - 1)]) * second; };
function direct(h: Harness, cred: Credential, assertion: string, clock: () => bigint, iat: number): Promise<Response> {
    const request = new Request(tokenUrl, post(grant(assertion), dpopProof(cred.key, {method: 'POST', url: tokenUrl, iat})));
    return issueAgentToken({db: h.db} as unknown as Env, request, clock);
}

test('token_ExpPassesBetweenValidationAndCommit_401', async () => {
    const h = await start();
    try {
        const cred = await register(h), now = nowSeconds();
        const assertion = clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl, iat: now, exp: now + 5});
        // Validated at now (exp in the future); the transaction runs at exactly exp.
        await refusal(await direct(h, cred, assertion, clockOf(now, now + 5), now), 401, 'invalid_client');
        assert.equal(await count(h, 'agent_access_tokens'), 0);
        assert.equal(await count(h, 'agent_assertions'), 0);
    } finally { await h.close(); }
});

test('token_JtiJustPastExpiry_KeptByCleanupAndReplayRefused', async () => {
    const h = await start();
    try {
        const cred = await register(h), now = nowSeconds();
        const first = clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl, iat: now, exp: now + 2, jti: 'first-assertion-00001'});
        assert.equal((await direct(h, cred, first, clockOf(now), now)).status, 200);
        await h.db.prepare("INSERT INTO agent_assertions(account_id,jti,expires_at,created_at) VALUES(?,'long-expired-assertion',CAST(? AS INTEGER),CAST(? AS INTEGER))")
            .bind(cred.accountId, ((BigInt(now) - 120n) * second).toString(), ((BigInt(now) - 200n) * second).toString()).run();
        // 30 s after the first assertion expired, another issuance runs cleanup.
        const later = now + 32;
        const other = clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl, iat: later, exp: later + 60});
        assert.equal((await direct(h, cred, other, clockOf(later), later)).status, 200);
        const jtis = (await h.db.prepare('SELECT jti FROM agent_assertions ORDER BY jti').all<{jti: string}>()).results.map(r => r.jti);
        assert.equal(jtis.includes('first-assertion-00001'), true);
        assert.equal(jtis.includes('long-expired-assertion'), false);
        // A Worker whose clock lags still sees the first assertion as unexpired; the retained jti refuses it.
        await refusal(await direct(h, cred, first, clockOf(now + 1), now + 1), 401, 'invalid_client');
        assert.equal(await count(h, 'agent_access_tokens'), 2);
    } finally { await h.close(); }
});

test('token_AuthorizationHeader_400InvalidRequest', async () => {
    const h = await start();
    try {
        const cred = await register(h);
        const init = post(grant(clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl})), dpopProof(cred.key, {method: 'POST', url: tokenUrl}));
        (init.headers as Record<string, string>).Authorization = 'Basic ' + Buffer.from(cred.accountId + ':x').toString('base64');
        await refusal(await h.mf.dispatchFetch(tokenUrl, init), 400, 'invalid_request');
        assert.equal(await count(h, 'agent_access_tokens'), 0);
    } finally { await h.close(); }
});

test('token_StorageError_503LogsErrorClassOnly', async t => {
    const cred = {accountId: 'account-with-secret-marker', key: agentKey()}, now = nowSeconds();
    const assertion = clientAssertion(cred.key, {accountId: cred.accountId, audience: tokenUrl, jti: 'secret-jti-marker-0001'});
    const proof = dpopProof(cred.key, {method: 'POST', url: tokenUrl});
    const failing = {prepare: () => { throw new TypeError('D1 unavailable'); }};
    const logged = t.mock.method(console, 'error', () => {});
    const response = await issueAgentToken({db: failing} as unknown as Env, new Request(tokenUrl, post(grant(assertion), proof)), () => BigInt(now) * second);
    await refusal(response, 503, 'temporarily_unavailable');
    const text = logged.mock.calls.map(call => call.arguments.map(String).join(' ')).join('\n');
    assert.equal(logged.mock.callCount(), 1);
    assert.match(text, /agent-token-unavailable/);
    assert.match(text, /TypeError/);
    assert.match(text, /D1 unavailable/);
    for (const secret of [assertion, proof, 'secret-jti-marker', cred.accountId, cred.key.privateJwk.d!]) assert.equal(text.includes(secret), false, secret);
});
