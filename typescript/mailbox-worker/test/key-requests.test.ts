import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { agentKey, clientAssertion, dpopProof, keyEndorsement, keyProof, type TestKey } from './agent-key.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import { issueRecoveryCode, recoverAgentKey } from '../src/recovery-codes.ts';
import { nowUtc } from '../src/time.ts';
import { Problem } from '../src/wire.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type View = {requestId: string; agentId: string; kid: string; label: string | null; endorsedBy: string; status: string; createdAt: string; expiresAt: string; decidedAt: string | null};
type Summary = {kid: string; label: string | null; createdAt: string; lastUsedAt: string | null; coolingUntil: string | null};

const origin = 'http://localhost';
const keysUrl = origin + '/v1/keys';
const recoveryUrl = origin + '/v1/key-requests';
const tokenUrl = origin + '/v1/token';

function env(h: Harness): Env {
    return {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100'} as unknown as Env;
}
/** A human-owned agent with no keys, as `/account` creates it. */
async function owned(h: Harness, subject = '123'): Promise<{agentId: string; humanId: string}> {
    const human = await signInHuman(env(h), {provider: 'github', subject, providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'person' + subject});
    assert.equal(human.ok, true);
    const humanId = (human as {humanId: string}).humanId;
    const agent = await createOwnedAgent(env(h), {humanId, displayName: 'owned', sourceIp: '203.0.113.7'});
    assert.equal(agent.ok, true);
    return {agentId: (agent as {accountId: string}).accountId, humanId};
}
const ownedAgent = async (h: Harness) => (await owned(h)).agentId;
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
async function issued(h: Harness, agent: {agentId: string; humanId: string}, clock?: () => bigint): Promise<string> {
    const result = await issueRecoveryCode(env(h), agent, clock);
    assert.equal(result.ok, true, JSON.stringify(result));
    return (result as {code: string}).code;
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
const liveKeys = async (h: Harness, accountId: string) => Number(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE account_id=? AND revoked_at IS NULL').bind(accountId).first('n'));
const codeUsed = async (h: Harness, accountId: string) => await h.db.prepare('SELECT used_at IS NOT NULL used FROM recovery_codes WHERE account_id=?').bind(accountId).first('used');
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
        const insert = (requestId: string, kid: string, status: string, old = now - 31n * day) => h.db.prepare(`INSERT INTO key_requests(request_id,account_id,kid,public_jwk,endorsed_by,status,created_at,expires_at,updated_at,decided_at)
            VALUES(?,?,?,'{}',?,?,CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER))`)
            .bind(requestId, seed.accountId, kid, seed.key.kid, status, old.toString(), (old + day).toString(), old.toString(), status === 'pending' ? null : (old + 1n).toString()).run();
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

test('addKey_OwnedAgentRetryAtLiveKeyLimit_Same202', async () => {
    const h = await start();
    try {
        const seed = await seedKey(h, await ownedAgent(h)), newKey = agentKey();
        const first = await view(await addKey(h, seed, newKey), 202);
        const now = (BigInt(Date.now()) * 10000n).toString();
        for (let i = 0; i < 99; i++) await h.db.prepare("INSERT INTO agent_keys(kid,account_id,public_jwk,created_at,updated_at) VALUES(?,?,'{}',CAST(? AS INTEGER),CAST(? AS INTEGER))")
            .bind('f'.repeat(40) + String(i).padStart(3, '0'), seed.accountId, now, now).run();
        assert.deepEqual(await view(await addKey(h, seed, newKey), 202), first);
        await problem(await addKey(h, seed, agentKey()), 429, {code: 'capacity'});
    } finally { await h.close(); }
});

test('issueRecoveryCode_Owner_StoresOnlyHashFor10Minutes', async () => {
    const h = await start();
    try {
        const agent = await owned(h), at = nowUtc();
        const result = await issueRecoveryCode(env(h), agent, () => at);
        assert.equal(result.ok, true);
        const {code, expiresAt} = result as {code: string; expiresAt: string};
        assert.match(code, /^[A-Za-z0-9_-]{43}$/);
        assert.equal(Date.parse(expiresAt) - Number(at / 10000n), 600_000);
        const row = await h.db.prepare('SELECT code_hash,CAST(used_at AS TEXT) used_at FROM recovery_codes WHERE account_id=?').bind(agent.agentId).first<{code_hash: string; used_at: string | null}>();
        assert.equal(row!.code_hash, createHash('sha256').update(code).digest('hex'));
        assert.equal(row!.used_at, null);
    } finally { await h.close(); }
});

test('issueRecoveryCode_NotOwnerOwnerlessFrozenOrReadOnly_Refused', async () => {
    const h = await start();
    try {
        const agent = await owned(h, '1'), other = await owned(h, '2'), ownerless = await h.register();
        assert.deepEqual(await issueRecoveryCode(env(h), {humanId: other.humanId, agentId: agent.agentId}), {ok: false, reason: 'not-found'});
        assert.deepEqual(await issueRecoveryCode(env(h), {humanId: agent.humanId, agentId: ownerless.accountId}), {ok: false, reason: 'not-found'});
        assert.deepEqual(await issueRecoveryCode(env(h), {humanId: agent.humanId, agentId: crypto.randomUUID()}), {ok: false, reason: 'not-found'});
        assert.deepEqual(await issueRecoveryCode({...env(h), RECOVERY_READ_ONLY: 'true'} as Env, agent), {ok: false, reason: 'unavailable'});
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(agent.humanId).run();
        assert.deepEqual(await issueRecoveryCode(env(h), agent), {ok: false, reason: 'frozen'});
        assert.equal(Number(await h.db.prepare('SELECT count(*) n FROM recovery_codes').first('n')), 0);
    } finally { await h.close(); }
});

test('recovery_OwnerCode_200LiveKeyWithoutCooling', async () => {
    const h = await start();
    try {
        const agent = await owned(h), newKey = agentKey(), code = await issued(h, agent);
        const response = await recover(h, newKey, {agentId: agent.agentId, code, label: 'laptop'});
        assert.equal(response.status, 200, await response.clone().text());
        const summary = await response.json() as Summary;
        assert.deepEqual(Object.keys(summary).sort(), ['coolingUntil', 'createdAt', 'kid', 'label', 'lastUsedAt']);
        assert.equal(summary.kid, newKey.kid);
        assert.equal(summary.label, 'laptop');
        assert.equal(summary.coolingUntil, null);
        assert.equal(await codeUsed(h, agent.agentId), 1);
        assert.equal(await requests(h), 0);
        const listed = await h.request('/v1/keys', {accountId: agent.agentId, key: newKey});
        assert.equal(listed.status, 200);
        assert.deepEqual((await listed.json() as {items: Summary[]}).items.map(k => k.kid), [newKey.kid]);
    } finally { await h.close(); }
});

test('recovery_OwnedAgentWithLiveKey_OwnerDecides200', async () => {
    const h = await start();
    try {
        const agent = await owned(h);
        await seedKey(h, agent.agentId);
        await view(await recover(h, agentKey(), {agentId: agent.agentId, code: await issued(h, agent)}), 200);
        assert.equal(await liveKeys(h, agent.agentId), 2);
    } finally { await h.close(); }
});

test('recovery_UnknownWrongExpiredUsedReplacedOwnerlessFrozen_Identical404NothingWritten', async () => {
    const h = await start();
    try {
        const agent = await owned(h, '1'), sibling = await owned(h, '1'), ownerless = await h.register(), stranger = await owned(h, '2');
        const replaced = await issued(h, agent), code = await issued(h, agent);
        const expired = await issued(h, sibling, () => nowUtc() - 6_000_000_001n);
        const strangerCode = await issued(h, stranger);
        const attempts: {agentId: string; code: string}[] = [
            {agentId: crypto.randomUUID(), code}, {agentId: sibling.agentId, code}, {agentId: ownerless.accountId, code},
            {agentId: agent.agentId, code: 'A'.repeat(43)}, {agentId: agent.agentId, code: 'short'}, {agentId: agent.agentId, code: replaced},
            {agentId: sibling.agentId, code: expired}, {agentId: agent.agentId, code: strangerCode},
        ];
        const bodies: string[] = [];
        for (const body of attempts) bodies.push(await problem(await recover(h, agentKey(), body), 404, {code: 'unavailable'}));
        assert.equal(await codeUsed(h, agent.agentId), 0);
        // The current code works once; a replay, even with another key, is the same 404.
        await view(await recover(h, agentKey(), {agentId: agent.agentId, code}), 200);
        bodies.push(await problem(await recover(h, agentKey(), {agentId: agent.agentId, code}), 404, {code: 'unavailable'}));
        // A frozen owner's outstanding code is refused.
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(stranger.humanId).run();
        bodies.push(await problem(await recover(h, agentKey(), {agentId: stranger.agentId, code: strangerCode}), 404, {code: 'unavailable'}));
        assert.equal(new Set(bodies).size, 1);
        assert.equal(await liveKeys(h, agent.agentId), 1);
        for (const id of [sibling.agentId, stranger.agentId]) assert.equal(await liveKeys(h, id), 0);
        assert.equal(await liveKeys(h, ownerless.accountId), 1);
        assert.equal(await codeUsed(h, stranger.agentId), 0);
        assert.equal(await requests(h), 0);
    } finally { await h.close(); }
});

test('recovery_ConcurrentSameCode_ExactlyOneKeyAdded', async () => {
    const h = await start();
    try {
        const agent = await owned(h), code = await issued(h, agent);
        const attempt = () => {
            const key = agentKey();
            return recoverAgentKey({db: h.db} as unknown as Env, new Request(recoveryUrl, {method: 'POST', headers: {DPoP: dpopProof(key, {method: 'POST', url: recoveryUrl})}}),
                {agentId: agent.agentId, code});
        };
        const outcomes = await Promise.allSettled([attempt(), attempt()]);
        const statuses = outcomes.map(o => o.status === 'fulfilled' ? 200 : o.reason instanceof Problem ? o.reason.status : 0).sort();
        assert.deepEqual(statuses, [200, 404]);
        assert.equal(await liveKeys(h, agent.agentId), 1);
    } finally { await h.close(); }
});

test('recovery_KidOfAnotherAgent_409KeyInUseCodeKept', async () => {
    const h = await start();
    try {
        const agent = await owned(h), other = await h.register(), code = await issued(h, agent);
        await problem(await recover(h, other.key, {agentId: agent.agentId, code}), 409, {code: 'conflict', field: 'proof', reason: 'key-in-use'});
        assert.equal(await codeUsed(h, agent.agentId), 0);
        await view(await recover(h, agentKey(), {agentId: agent.agentId, code}), 200);
    } finally { await h.close(); }
});

test('recovery_TwentyFirstAttemptFromOneSource_429', async () => {
    const h = await start();
    try {
        const agent = await owned(h), code = await issued(h, agent), source = {'CF-Connecting-IP': '198.51.100.1'};
        for (let i = 0; i < 20; i++) await problem(await recover(h, agentKey(), {agentId: agent.agentId, code: 'A'.repeat(43)}, {headers: source}), 404, {code: 'unavailable'});
        const limited = await recover(h, agentKey(), {agentId: agent.agentId, code}, {headers: source});
        const retryAfter = Number(limited.headers.get('Retry-After'));
        await problem(limited, 429, {code: 'capacity'});
        assert.ok(retryAfter >= 1 && retryAfter <= 3600, String(retryAfter));
        assert.equal(await codeUsed(h, agent.agentId), 0);
        await view(await recover(h, agentKey(), {agentId: agent.agentId, code}, {headers: {'CF-Connecting-IP': '198.51.100.2'}}), 200);
    } finally { await h.close(); }
});

test('recovery_AuthorizationHeader_400', async () => {
    const h = await start();
    try {
        const agent = await owned(h), code = await issued(h, agent);
        await problem(await recover(h, agentKey(), {agentId: agent.agentId, code}, {headers: {Authorization: 'DPoP ' + 'a'.repeat(43)}}), 400, {code: 'invalid'});
        assert.equal(await codeUsed(h, agent.agentId), 0);
    } finally { await h.close(); }
});

test('recovery_ProofWithAthOrWrongHtu_401', async () => {
    const h = await start();
    try {
        const agent = await owned(h), code = await issued(h, agent), key = agentKey();
        for (const proof of [dpopProof(key, {method: 'POST', url: recoveryUrl, accessToken: 'a'.repeat(43)}), dpopProof(key, {method: 'POST', url: keysUrl}),
            dpopProof(key, {method: 'GET', url: recoveryUrl})])
            await problem(await recover(h, key, {agentId: agent.agentId, code}, {proof}), 401, {code: 'unauthenticated'});
        await problem(await h.mf.dispatchFetch(recoveryUrl, {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({agentId: agent.agentId, code})}) as unknown as Response,
            401, {code: 'unauthenticated'});
        assert.equal(await codeUsed(h, agent.agentId), 0);
    } finally { await h.close(); }
});

test('recovery_ProofJwkWithD_401NothingStored', async () => {
    const h = await start();
    try {
        const agent = await owned(h), code = await issued(h, agent), key = agentKey();
        const proof = dpopProof(key, {method: 'POST', url: recoveryUrl, header: {jwk: {...key.publicJwk, d: key.privateJwk.d}}});
        const text = await problem(await recover(h, key, {agentId: agent.agentId, code}, {proof}), 401, {code: 'unauthenticated'});
        assert.ok(!text.includes(key.privateJwk.d!));
        assert.equal(await keyRows(h, key.kid), 0);
        assert.equal(await codeUsed(h, agent.agentId), 0);
    } finally { await h.close(); }
});

test('recovery_ExtraOrMalformedBody_400', async () => {
    const h = await start();
    try {
        const agent = await owned(h), code = await issued(h, agent);
        for (const body of [{agentId: agent.agentId, code, kid: 'x'}, {agentId: agent.agentId}, {code}, {agentId: 7, code}, {agentId: agent.agentId, code: 7},
            {agentId: agent.agentId, code, label: ''}])
            await problem(await recover(h, agentKey(), body), 400, {code: 'invalid'});
        assert.equal(await codeUsed(h, agent.agentId), 0);
    } finally { await h.close(); }
});

test('recovery_ReadOnlyRecovery_503', async () => {
    const h = await start({bindings: {RECOVERY_READ_ONLY: 'true'}});
    try {
        const agent = await owned(h), code = await issued(h, agent);
        await problem(await recover(h, agentKey(), {agentId: agent.agentId, code}), 503, {code: 'maintenance'});
        assert.equal(await codeUsed(h, agent.agentId), 0);
    } finally { await h.close(); }
});
