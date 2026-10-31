import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { start } from './harness.ts';
import { agentKey, dpopProof, nowSeconds, type TestKey } from './agent-key.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import { findKey } from '../src/agent-keys.ts';
import { Problem } from '../src/wire.ts';
import type { Database } from '../src/database.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const url = 'http://localhost/v1/accounts';

function registerKey(h: Harness, key: TestKey, body: object = {}, proof?: string, headers: Record<string, string> = {}): Promise<Response> {
    return h.mf.dispatchFetch(url, {
        method: 'POST',
        headers: {'Content-Type': 'application/json', DPoP: proof ?? dpopProof(key, {method: 'POST', url}), ...headers},
        body: JSON.stringify(body),
    }) as unknown as Promise<Response>;
}
async function registered(h: Harness, key: TestKey, body: object = {}, headers: Record<string, string> = {}) {
    const response = await registerKey(h, key, body, undefined, headers);
    assert.equal(response.status, 200, await response.clone().text());
    return response.json() as Promise<{agentId: string; accountId: string; displayName: string | null; description: string | null; kid: string}>;
}
const count = async (h: Harness, table: string) => Number(await h.db.prepare(`SELECT count(*) n FROM ${table}`).first('n'));
const environment = (h: Harness, overrides: Partial<Env> = {}) => ({
    db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100',
    REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100', ...overrides }) as unknown as Env;

test('registerAgent_NewKey_CreatesOwnerlessAgentWithSettledKey', async () => {
    const h = await start();
    try {
        const key = agentKey();
        const result = await registered(h, key, {displayName: 'Builder', description: 'Builds', label: 'laptop'});
        assert.equal(result.kid, key.kid);
        assert.equal(result.agentId, result.accountId);
        assert.equal(result.displayName, 'Builder');
        assert.equal(result.description, 'Builds');
        const row = await h.db.prepare('SELECT account_id,public_jwk,label,cooling_until,revoked_at,last_used_at FROM agent_keys WHERE kid=?').bind(key.kid).first<any>();
        assert.equal(row.account_id, result.agentId);
        assert.equal(row.label, 'laptop');
        assert.equal(row.cooling_until, null);
        assert.equal(row.revoked_at, null);
        assert.equal(row.last_used_at, null);
        assert.deepEqual(JSON.parse(row.public_jwk), {crv: 'Ed25519', kty: 'OKP', x: key.publicJwk.x});
        assert.equal(await h.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(result.agentId).first('human_id'), null);
    } finally { await h.close(); }
});

test('registerAgent_SameKeyRetry_ReturnsSameAgentIgnoringProfile', async () => {
    const h = await start();
    try {
        const key = agentKey();
        const first = await registered(h, key, {displayName: 'First'});
        const second = await registered(h, key, {displayName: 'Second', label: 'other'});
        assert.deepEqual(second, first);
        assert.equal(await count(h, 'accounts'), 1);
        assert.equal(await h.db.prepare('SELECT label FROM agent_keys WHERE kid=?').bind(key.kid).first('label'), null);
    } finally { await h.close(); }
});

test('registerAgent_ConcurrentSameKey_OneAgent', async () => {
    const h = await start();
    try {
        const key = agentKey();
        const [a, b] = await Promise.all([registered(h, key), registered(h, key)]);
        assert.equal(a.agentId, b.agentId);
        assert.equal(await count(h, 'accounts'), 1);
        assert.equal(await count(h, 'agent_keys'), 1);
    } finally { await h.close(); }
});

test('registerAgent_ProofJwkWithD_401AndNothingStored', async () => {
    const h = await start();
    try {
        const key = agentKey();
        const response = await registerKey(h, key, {}, dpopProof(key, {method: 'POST', url, header: {jwk: {...key.publicJwk, d: key.privateJwk.d}}}));
        assert.equal(response.status, 401);
        assert.equal((await response.text()).includes(key.privateJwk.d!), false);
        assert.equal(await count(h, 'accounts'), 0);
        assert.equal(await count(h, 'agent_keys'), 0);
    } finally { await h.close(); }
});

test('registerAgent_ExtraPublicJwkMembers_StoresCanonical', async () => {
    const h = await start();
    try {
        const key = agentKey();
        const proof = dpopProof(key, {method: 'POST', url, header: {jwk: {...key.publicJwk, kid: key.kid, alg: 'EdDSA', use: 'sig'}}});
        const response = await registerKey(h, key, {}, proof);
        assert.equal(response.status, 200, await response.clone().text());
        const stored = await h.db.prepare('SELECT public_jwk FROM agent_keys WHERE kid=?').bind(key.kid).first<string>('public_jwk');
        assert.equal(stored, JSON.stringify({crv: 'Ed25519', kty: 'OKP', x: key.publicJwk.x}));
    } finally { await h.close(); }
});

test('registerAgent_StaleProofOrWrongHtu_401', async () => {
    const h = await start();
    try {
        const key = agentKey();
        for (const proof of [
            dpopProof(key, {method: 'POST', url, iat: nowSeconds() - 120}),
            dpopProof(key, {method: 'POST', url: 'http://localhost/v1/token'}),
            dpopProof(key, {method: 'GET', url}),
        ]) assert.equal((await registerKey(h, key, {}, proof)).status, 401);
        const missing = await h.mf.dispatchFetch(url, {method: 'POST', headers: {'Content-Type': 'application/json', DPoP: 'not-a-jws'}, body: '{}'});
        assert.equal(missing.status, 401);
        assert.equal(await count(h, 'accounts'), 0);
    } finally { await h.close(); }
});

test('registerAgent_RevokedKey_401', async () => {
    const h = await start();
    try {
        const key = agentKey();
        await registered(h, key);
        await h.db.prepare('UPDATE agent_keys SET revoked_at=created_at WHERE kid=?').bind(key.kid).run();
        const response = await registerKey(h, key);
        assert.equal(response.status, 401);
        assert.equal(await count(h, 'accounts'), 1);
    } finally { await h.close(); }
});

test('registerAgent_AuthorizationHeader_400', async () => {
    const h = await start();
    try {
        const key = agentKey();
        const response = await registerKey(h, key, {}, undefined, {Authorization: 'DPoP token'});
        assert.equal(response.status, 400);
        assert.equal(await count(h, 'accounts'), 0);
    } finally { await h.close(); }
});

test('registerAgent_InvalidLabel_400', async () => {
    const h = await start();
    try {
        const key = agentKey();
        for (const label of ['', 'x'.repeat(65), 'a\u0007', 7]) {
            const response = await registerKey(h, key, {label});
            assert.equal(response.status, 400, JSON.stringify(label));
            assert.equal((await response.json() as {field: string}).field, 'label');
        }
        assert.equal(await count(h, 'accounts'), 0);
    } finally { await h.close(); }
});

test('registerAgent_RegistrationDisabled_NewKey404ExistingKeyReturns', async () => {
    const persist = await mkdtemp(join(tmpdir(), 'weave-d1-'));
    let h = await start({persist});
    try {
        const key = agentKey();
        const first = await registered(h, key);
        await h.close();
        h = await start({persist, migrate: false, bindings: {REGISTRATION_ENABLED: 'false'}});
        assert.deepEqual(await registered(h, key), first);
        assert.equal((await registerKey(h, agentKey())).status, 404);
        assert.equal(await count(h, 'accounts'), 1);
    } finally { await h.close(); await rm(persist, {recursive: true, force: true}); }
});

test('registerAgent_RecoveryReadOnly_NewKey503ExistingKeyReturns', async () => {
    const persist = await mkdtemp(join(tmpdir(), 'weave-d1-'));
    let h = await start({persist});
    try {
        const key = agentKey();
        const first = await registered(h, key);
        await h.close();
        h = await start({persist, migrate: false, bindings: {RECOVERY_READ_ONLY: 'true'}});
        assert.deepEqual(await registered(h, key), first);
        assert.equal((await registerKey(h, agentKey())).status, 503);
        assert.equal(await count(h, 'accounts'), 1);
    } finally { await h.close(); await rm(persist, {recursive: true, force: true}); }
});

test('registerAgent_CapacityCountsSelfRegisteredOnly', async () => {
    const h = await start({bindings: {REGISTRATION_MAX_ACCOUNTS: '2'}});
    try {
        const env = environment(h);
        const human = await signInHuman(env, {provider: 'github', subject: '123', providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'alice'});
        assert.equal(human.ok, true);
        const humanId = (human as {humanId: string}).humanId;
        for (let i = 0; i < 3; i++) assert.equal((await createOwnedAgent(env, {humanId, displayName: 'owned ' + i, sourceIp: '203.0.113.7'})).ok, true);
        await registered(h, agentKey());
        await registered(h, agentKey());
        const refused = await registerKey(h, agentKey());
        assert.equal(refused.status, 429);
        assert.equal((await refused.json() as {code: string}).code, 'capacity');
        assert.equal(await count(h, 'accounts'), 5);
    } finally { await h.close(); }
});

test('registerAgent_HourlyPerIpLimit_429', async () => {
    const h = await start({bindings: {REGISTRATION_PER_IP_PER_HOUR: '1'}});
    try {
        const source = {'CF-Connecting-IP': '198.51.100.9'};
        await registered(h, agentKey(), {}, source);
        const refused = await registerKey(h, agentKey(), {}, undefined, source);
        assert.equal(refused.status, 429);
        assert.equal((await refused.json() as {code: string}).code, 'capacity');
        assert.equal(await count(h, 'accounts'), 1);
    } finally { await h.close(); }
});

test('registerAgent_SameKeyRetries_ChargeNoRateLimit', async () => {
    const h = await start({bindings: {REGISTRATION_PER_IP_PER_HOUR: '2'}});
    try {
        const source = {'CF-Connecting-IP': '198.51.100.10'};
        const a = agentKey();
        const first = await registered(h, a, {}, source);
        assert.deepEqual(await registered(h, a, {}, source), first);
        assert.deepEqual(await registered(h, a, {}, source), first);
        await registered(h, agentKey(), {}, source);
        assert.equal(await count(h, 'accounts'), 2);
    } finally { await h.close(); }
});

test('registerAgent_ProofWithAth_401', async () => {
    const h = await start();
    try {
        const key = agentKey();
        const response = await registerKey(h, key, {}, dpopProof(key, {method: 'POST', url, claims: {ath: 'A'.repeat(43)}}));
        assert.equal(response.status, 401);
        assert.equal(await count(h, 'accounts'), 0);
    } finally { await h.close(); }
});

test('findKey_CorruptStoredJwk_503Storage', async () => {
    const row = {kid: 'k'.repeat(43), account_id: 'a', label: null, created_at: '1', last_used_at: null, revoked_at: null, cooling_until: null};
    for (const public_jwk of ['{not json', '{}']) {
        const db = {prepare: () => ({bind: () => ({first: async () => ({...row, public_jwk})})})} as unknown as Database;
        await assert.rejects(findKey(db, row.kid), (error: unknown) => error instanceof Problem && error.status === 503 && error.code === 'storage');
    }
});
