import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { agentKey, clientAssertion, dpopProof, keyEndorsement, keyProof, type TestKey } from './agent-key.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type View = {requestId: string; agentId: string; kid: string; label: string | null; endorsedBy: string | null; status: string; createdAt: string; expiresAt: string; decidedAt: string | null};

const origin = 'http://localhost';
const keysUrl = origin + '/v1/keys';
const recoveryUrl = origin + '/v1/key-requests';
const tokenUrl = origin + '/v1/token';

function env(h: Harness): Env {
    return {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100'} as unknown as Env;
}
/** A human-owned agent with no keys, as `/account` creates it. */
async function ownedAgent(h: Harness): Promise<string> {
    const human = await signInHuman(env(h), {provider: 'github', subject: '123', providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'alice'});
    assert.equal(human.ok, true);
    const owned = await createOwnedAgent(env(h), {humanId: (human as {humanId: string}).humanId, displayName: 'owned', sourceIp: '203.0.113.7'});
    assert.equal(owned.ok, true);
    return (owned as {accountId: string}).accountId;
}
/** Inserts a live key on the agent directly, standing in for an owner-confirmed key. */
async function seedKey(h: Harness, accountId: string, coolingUntil: bigint | null = null): Promise<AgentCredential> {
    const key = agentKey(), now = BigInt(Date.now()) * 10000n;
    await h.db.prepare('INSERT INTO agent_keys(kid,account_id,public_jwk,created_at,cooling_until,updated_at) VALUES(?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER))')
        .bind(key.kid, accountId, JSON.stringify(key.publicJwk), now.toString(), coolingUntil === null ? null : coolingUntil.toString(), now.toString()).run();
    return {accountId, key};
}
function addKey(h: Harness, cred: AgentCredential, newKey: TestKey): Promise<Response> {
    return h.request('/v1/keys', cred, 'POST', {proof: keyProof(newKey, {accountId: cred.accountId, audience: keysUrl}),
        endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: newKey.kid})}) as unknown as Promise<Response>;
}
async function view(response: Response, status: number): Promise<View> {
    const text = await response.text();
    assert.equal(response.status, status, text);
    return JSON.parse(text) as View;
}
function recover(h: Harness, key: TestKey, body: unknown, options: {proof?: string; headers?: Record<string, string>} = {}): Promise<Response> {
    return h.mf.dispatchFetch(recoveryUrl, {method: 'POST', headers: {'Content-Type': 'application/json', DPoP: options.proof ?? dpopProof(key, {method: 'POST', url: recoveryUrl}),
        ...options.headers}, body: JSON.stringify(body)}) as unknown as Promise<Response>;
}
async function problem(response: Response, status: number, expected: Record<string, unknown>): Promise<string> {
    const text = await response.text();
    assert.equal(response.status, status, text);
    const value = JSON.parse(text) as Record<string, unknown>;
    for (const [name, wanted] of Object.entries(expected)) assert.equal(value[name], wanted, text);
    return text;
}
const requests = async (h: Harness, accountId?: string) => Number(accountId === undefined
    ? await h.db.prepare('SELECT count(*) n FROM key_requests').first('n')
    : await h.db.prepare('SELECT count(*) n FROM key_requests WHERE account_id=?').bind(accountId).first('n'));
const keyRows = async (h: Harness, kid: string) => Number(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE kid=?').bind(kid).first('n'));

test('addKey_OwnedAgent_202PendingNotUsable', async () => {
    const h = await start();
    try {
        const seed = await seedKey(h, await ownedAgent(h)), newKey = agentKey();
        const pending = await view(await addKey(h, seed, newKey), 202);
        assert.deepEqual(Object.keys(pending).sort(), ['accountId', 'agentId', 'createdAt', 'decidedAt', 'endorsedBy', 'expiresAt', 'kid', 'label', 'requestId', 'status']);
        assert.equal(pending.status, 'pending');
        assert.equal(pending.agentId, seed.accountId);
        assert.equal(pending.kid, newKey.kid);
        assert.equal(pending.endorsedBy, seed.key.kid);
        assert.equal(pending.decidedAt, null);
        assert.equal(Date.parse(pending.expiresAt) - Date.parse(pending.createdAt), 86_400_000);
        assert.equal(await keyRows(h, newKey.kid), 0);
        const assertion = clientAssertion(newKey, {accountId: seed.accountId, audience: tokenUrl});
        const token = await h.mf.dispatchFetch(tokenUrl, {method: 'POST', headers: {'Content-Type': 'application/x-www-form-urlencoded', DPoP: dpopProof(newKey, {method: 'POST', url: tokenUrl})},
            body: new URLSearchParams([['grant_type', 'client_credentials'], ['client_assertion_type', 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'], ['client_assertion', assertion]]).toString()});
        assert.equal(token.status, 401);
        assert.equal((await token.json() as {error: string}).error, 'invalid_client');
    } finally { await h.close(); }
});

test('addKey_OwnedAgentRetry_SamePendingRequest', async () => {
    const h = await start();
    try {
        const seed = await seedKey(h, await ownedAgent(h)), newKey = agentKey();
        const first = await view(await addKey(h, seed, newKey), 202), second = await view(await addKey(h, seed, newKey), 202);
        assert.deepEqual(second, first);
        assert.equal(await requests(h, seed.accountId), 1);
    } finally { await h.close(); }
});

test('addKey_OwnedAgentFourthPending_429', async () => {
    const h = await start();
    try {
        const seed = await seedKey(h, await ownedAgent(h));
        for (let i = 0; i < 3; i++) assert.equal((await view(await addKey(h, seed, agentKey()), 202)).status, 'pending');
        await problem(await addKey(h, seed, agentKey()), 429, {code: 'capacity'});
        assert.equal(await requests(h, seed.accountId), 3);
    } finally { await h.close(); }
});

test('addKey_OwnedAgentExpiredPending_FreesASlot', async () => {
    const h = await start();
    try {
        const seed = await seedKey(h, await ownedAgent(h)), keys = [agentKey(), agentKey(), agentKey()];
        const first = await view(await addKey(h, seed, keys[0]), 202);
        for (const key of keys.slice(1)) await view(await addKey(h, seed, key), 202);
        await h.db.prepare('UPDATE key_requests SET expires_at=created_at+1 WHERE request_id=?').bind(first.requestId).run();
        const again = await view(await addKey(h, seed, keys[0]), 202);
        assert.notEqual(again.requestId, first.requestId);
        assert.equal(await h.db.prepare('SELECT status FROM key_requests WHERE request_id=?').bind(first.requestId).first('status'), 'expired');
        assert.equal(await requests(h, seed.accountId), 4);
    } finally { await h.close(); }
});

test('addKey_OwnedAgentEndorsedByCoolingKey_202', async () => {
    const h = await start();
    try {
        const seed = await seedKey(h, await ownedAgent(h), (BigInt(Date.now()) + 3_600_000n) * 10000n), newKey = agentKey();
        const pending = await view(await addKey(h, seed, newKey), 202);
        assert.equal(pending.endorsedBy, seed.key.kid);
    } finally { await h.close(); }
});

test('addKey_OwnedAgentKidOfAnotherAgent_409KeyInUse', async () => {
    const h = await start();
    try {
        const seed = await seedKey(h, await ownedAgent(h)), other = await h.register();
        await problem(await addKey(h, seed, other.key), 409, {code: 'conflict', field: 'proof', reason: 'key-in-use'});
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('addKey_OwnedAgentOldTerminalRequests_PrunedPendingKept', async () => {
    const h = await start();
    try {
        const seed = await seedKey(h, await ownedAgent(h));
        const day = 864000000000n, now = BigInt(Date.now()) * 10000n;
        const insert = (requestId: string, kid: string, status: string, old = now - 31n * day) => h.db.prepare(`INSERT INTO key_requests(request_id,account_id,kid,public_jwk,status,created_at,expires_at,updated_at,decided_at)
            VALUES(?,?,?,'{}',?,CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER))`)
            .bind(requestId, seed.accountId, kid, status, old.toString(), (old + day).toString(), old.toString(), status === 'pending' ? null : (old + 1n).toString()).run();
        await insert('old-rejected', agentKey().kid, 'rejected');
        await insert('old-expired', agentKey().kid, 'expired');
        await insert('old-confirmed', agentKey().kid, 'confirmed');
        await insert('old-stale', agentKey().kid, 'pending', now - 2n * day);
        await view(await addKey(h, seed, agentKey()), 202);
        const left = (await h.db.prepare('SELECT request_id FROM key_requests WHERE request_id LIKE ? ORDER BY request_id').bind('old-%').all<{request_id: string}>()).results;
        // The stale pending row is expired, not deleted; decided rows past retention are removed.
        assert.deepEqual(left.map((r: {request_id: string}) => r.request_id), ['old-stale']);
        assert.equal(await h.db.prepare("SELECT status FROM key_requests WHERE request_id='old-stale'").first('status'), 'expired');
    } finally { await h.close(); }
});

test('recovery_OwnedAgentWithoutKeys_202Pending', async () => {
    const h = await start();
    try {
        const agentId = await ownedAgent(h), newKey = agentKey();
        const pending = await view(await recover(h, newKey, {agentId, label: 'laptop'}), 202);
        assert.equal(pending.status, 'pending');
        assert.equal(pending.agentId, agentId);
        assert.equal(pending.kid, newKey.kid);
        assert.equal(pending.label, 'laptop');
        assert.equal(pending.endorsedBy, null);
        assert.deepEqual(await view(await recover(h, newKey, {agentId, label: 'laptop'}), 202), pending);
        assert.equal(await requests(h, agentId), 1);
        assert.equal(await keyRows(h, newKey.kid), 0);
    } finally { await h.close(); }
});

test('recovery_ConfirmedRetry_200ConfirmedView', async () => {
    const h = await start();
    try {
        const agentId = await ownedAgent(h), newKey = agentKey();
        const pending = await view(await recover(h, newKey, {agentId}), 202);
        // Stands in for the owner's confirmation (a later task): the key becomes live and the request confirmed.
        const now = (BigInt(Date.now()) * 10000n).toString();
        await h.db.prepare('INSERT INTO agent_keys(kid,account_id,public_jwk,created_at,updated_at) VALUES(?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))')
            .bind(newKey.kid, agentId, JSON.stringify(newKey.publicJwk), now, now).run();
        await h.db.prepare(`UPDATE key_requests SET status='confirmed',decided_at=CAST(? AS INTEGER) WHERE request_id=?`).bind(now, pending.requestId).run();
        const confirmed = await view(await recover(h, newKey, {agentId}), 200);
        assert.equal(confirmed.requestId, pending.requestId);
        assert.equal(confirmed.status, 'confirmed');
        assert.notEqual(confirmed.decidedAt, null);
    } finally { await h.close(); }
});

test('recovery_OwnedAgentWithLiveKey_404', async () => {
    const h = await start();
    try {
        const agentId = await ownedAgent(h);
        await seedKey(h, agentId);
        await problem(await recover(h, agentKey(), {agentId}), 404, {code: 'unavailable'});
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_OwnerlessAgent_404', async () => {
    const h = await start();
    try {
        const ownerless = await h.register();
        await problem(await recover(h, agentKey(), {agentId: ownerless.accountId}), 404, {code: 'unavailable'});
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_UnknownAgent_404', async () => {
    const h = await start();
    try {
        await problem(await recover(h, agentKey(), {agentId: crypto.randomUUID()}), 404, {code: 'unavailable'});
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_UnknownOwnerlessAndKeyedAgents_ByteIdenticalRefusals', async () => {
    const h = await start();
    try {
        const keyed = await ownedAgent(h), ownerless = await h.register(), inUse = await h.register();
        await seedKey(h, keyed);
        const bodies: string[] = [];
        for (const agentId of [keyed, ownerless.accountId, crypto.randomUUID()]) {
            bodies.push(await problem(await recover(h, agentKey(), {agentId}), 404, {code: 'unavailable'}));
            // A key already in use must not turn the refusal into an ownership oracle.
            bodies.push(await problem(await recover(h, inUse.key, {agentId}), 404, {code: 'unavailable'}));
        }
        assert.equal(new Set(bodies).size, 1);
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_KidOfAnotherAgent_409KeyInUse', async () => {
    const h = await start();
    try {
        const agentId = await ownedAgent(h), other = await h.register();
        await problem(await recover(h, other.key, {agentId}), 409, {code: 'conflict', field: 'proof', reason: 'key-in-use'});
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_AuthorizationHeader_400', async () => {
    const h = await start();
    try {
        const agentId = await ownedAgent(h);
        await problem(await recover(h, agentKey(), {agentId}, {headers: {Authorization: 'DPoP ' + 'a'.repeat(43)}}), 400, {code: 'invalid'});
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_ProofWithAthOrWrongHtu_401', async () => {
    const h = await start();
    try {
        const agentId = await ownedAgent(h), key = agentKey();
        for (const proof of [dpopProof(key, {method: 'POST', url: recoveryUrl, accessToken: 'a'.repeat(43)}), dpopProof(key, {method: 'POST', url: keysUrl}),
            dpopProof(key, {method: 'GET', url: recoveryUrl})])
            await problem(await recover(h, key, {agentId}, {proof}), 401, {code: 'unauthenticated'});
        await problem(await h.mf.dispatchFetch(recoveryUrl, {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({agentId})}) as unknown as Response,
            401, {code: 'unauthenticated'});
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_ProofJwkWithD_401NothingStored', async () => {
    const h = await start();
    try {
        const agentId = await ownedAgent(h), key = agentKey();
        const proof = dpopProof(key, {method: 'POST', url: recoveryUrl, header: {jwk: {...key.publicJwk, d: key.privateJwk.d}}});
        const text = await problem(await recover(h, key, {agentId}, {proof}), 401, {code: 'unauthenticated'});
        assert.ok(!text.includes(key.privateJwk.d!));
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_ExtraOrMalformedBody_400', async () => {
    const h = await start();
    try {
        const agentId = await ownedAgent(h);
        for (const body of [{agentId, kid: 'x'}, {}, {agentId: 7}, {agentId, label: ''}])
            await problem(await recover(h, agentKey(), body), 400, {code: 'invalid'});
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_ReadOnlyRecovery_503', async () => {
    const h = await start({bindings: {RECOVERY_READ_ONLY: 'true'}});
    try {
        const agentId = await ownedAgent(h);
        await problem(await recover(h, agentKey(), {agentId}), 503, {code: 'maintenance'});
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});
