import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { agentKey, bindingRequest, dpopProof, keyEndorsement, keyProof, type TestKey } from './agent-key.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import { ownerAction, ownerOverview, reauthFutureSkewMs, reauthWindowMs, type OwnerAction } from '../src/owner-actions.ts';
import { liveKeysDigest } from '../src/binding-requests.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Action = Omit<OwnerAction, 'humanId' | 'authAt'> & Partial<Pick<OwnerAction, 'authAt'>>;

const origin = 'http://localhost';
const bindingsUrl = origin + '/v1/binding-requests';
const keysUrl = origin + '/v1/keys';
const recoveryUrl = origin + '/v1/key-requests';

const environment = (h: Harness, overrides: Partial<Env> = {}) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100',
    REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100', ...overrides}) as unknown as Env;
async function human(h: Harness, subject: string): Promise<string> {
    const result = await signInHuman(environment(h), {provider: 'github', subject, providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'person' + subject});
    assert.equal(result.ok, true);
    return (result as {humanId: string}).humanId;
}
/** The digest of the agent's live keys as they are now, as the owner page would have shown them. */
const liveDigest = async (h: Harness, accountId: string) => liveKeysDigest((await h.db.prepare('SELECT kid FROM agent_keys WHERE account_id=? AND revoked_at IS NULL')
    .bind(accountId).all<{kid: string}>()).results.map(row => row.kid));
/** A confirm-binding without `keysDigest` confirms the key set the requesting agent has at the time of the call. */
async function act(h: Harness, humanId: string, action: Action, options: {clock?: () => bigint; env?: Env} = {}) {
    let input = {humanId, authAt: Date.now(), ...action} as Record<string, unknown>;
    if (input.kind === 'confirm-binding' && !('keysDigest' in input)) {
        const accountId = await h.db.prepare('SELECT account_id FROM binding_requests WHERE request_id=?').bind(input.requestId as string).first<string>('account_id');
        input = {...input, keysDigest: accountId === null ? 'A'.repeat(43) : await liveDigest(h, accountId)};
    }
    return ownerAction(options.env ?? environment(h), input as OwnerAction, options.clock);
}
const at = (ms: number) => () => BigInt(ms) * 10000n;

async function bind(h: Harness, cred: AgentCredential, subject: string): Promise<string> {
    const request = bindingRequest(cred.key, {accountId: cred.accountId, audience: bindingsUrl, human: 'github:' + subject});
    const response = await h.request('/v1/binding-requests', cred, 'POST', {request}) as unknown as Response;
    assert.equal(response.status, 200, await response.clone().text());
    return ((await response.json()) as {requestId: string}).requestId;
}
/** An ownerless agent with one settled key and a pending binding request naming the subject. */
async function requested(h: Harness, subject: string) {
    const cred = await h.register({displayName: 'Bot'});
    return {cred, requestId: await bind(h, cred, subject)};
}
const bindingStatus = async (h: Harness, requestId: string) => await h.db.prepare('SELECT status FROM binding_requests WHERE request_id=?').bind(requestId).first('status');
const ownerOf = async (h: Harness, accountId: string) => await h.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(accountId).first('human_id');
const expireNow = (h: Harness, table: 'binding_requests' | 'key_requests', requestId: string) =>
    h.db.prepare(`UPDATE ${table} SET expires_at=created_at+1 WHERE request_id=?`).bind(requestId).run();

async function seedKey(h: Harness, accountId: string, options: {createdAt?: bigint} = {}): Promise<AgentCredential> {
    const key = agentKey(), now = (options.createdAt ?? BigInt(Date.now()) * 10000n).toString();
    await h.db.prepare('INSERT INTO agent_keys(kid,account_id,public_jwk,created_at,cooling_until,updated_at) VALUES(?,?,?,CAST(? AS INTEGER),NULL,CAST(? AS INTEGER))')
        .bind(key.kid, accountId, JSON.stringify(key.publicJwk), now, now).run();
    return {accountId, key};
}
/** A human-owned agent with one settled key. */
async function ownedAgent(h: Harness, humanId: string, name = 'owned'): Promise<AgentCredential> {
    const created = await createOwnedAgent(environment(h), {humanId, displayName: name, sourceIp: '203.0.113.7'});
    assert.equal(created.ok, true);
    return seedKey(h, (created as {accountId: string}).accountId);
}
async function addKey(h: Harness, cred: AgentCredential, newKey: TestKey): Promise<string> {
    const response = await h.request('/v1/keys', cred, 'POST', {proof: keyProof(newKey, {accountId: cred.accountId, audience: keysUrl}),
        endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: newKey.kid})}) as unknown as Response;
    assert.equal(response.status, 202, await response.clone().text());
    return ((await response.json()) as {requestId: string}).requestId;
}
const keyRequestStatus = async (h: Harness, requestId: string) => await h.db.prepare('SELECT status FROM key_requests WHERE request_id=?').bind(requestId).first('status');
const revokeInStorage = (h: Harness, kid: string) => h.db.prepare('UPDATE agent_keys SET revoked_at=created_at+1 WHERE kid=?').bind(kid).run();

test('overview_PendingBindingForSubject_ShowsAgentNameRequestingKidAndLiveKeys', async () => {
    const h = await start();
    try {
        const cred = await h.register({displayName: 'Bot'});
        const second = agentKey();
        const added = await h.request('/v1/keys', cred, 'POST', {proof: keyProof(second, {accountId: cred.accountId, audience: keysUrl}),
            endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: second.kid})}) as unknown as Response;
        assert.equal(added.status, 200, await added.clone().text());
        const requestId = await bind(h, cred, '555');
        await bind(h, await h.register(), '999');
        const overview = await ownerOverview(environment(h), await human(h, '555'));
        assert.equal(overview.frozen, false);
        if (overview.frozen) return;
        assert.equal(overview.bindingRequests.length, 1);
        const [request] = overview.bindingRequests;
        assert.equal(request.requestId, requestId);
        assert.equal(request.agentId, cred.accountId);
        assert.equal(request.displayName, 'Bot');
        assert.equal(request.requestingKid, cred.key.kid);
        assert.deepEqual(request.liveKeys.map(key => key.kid), [cred.key.kid, second.kid]);
        assert.equal(request.liveKeys[0].coolingUntil, null);
        assert.notEqual(request.liveKeys[1].coolingUntil, null);
        assert.deepEqual(overview.agents, []);
    } finally { await h.close(); }
});

test('overview_ExpiredRequestAndUnknownHuman_Empty', async () => {
    const h = await start();
    try {
        const {requestId} = await requested(h, '555');
        const id = await human(h, '555');
        await expireNow(h, 'binding_requests', requestId);
        assert.deepEqual(await ownerOverview(environment(h), id), {frozen: false, bindingRequests: [], agents: []});
        assert.deepEqual(await ownerOverview(environment(h), crypto.randomUUID()), {frozen: false, bindingRequests: [], agents: []});
    } finally { await h.close(); }
});

test('overview_OwnedAgent_ListsLiveKeysAndPendingKeyRequestsOnly', async () => {
    const h = await start();
    try {
        const id = await human(h, '555');
        const cred = await ownedAgent(h, id);
        const pending = await addKey(h, cred, agentKey()), expired = await addKey(h, cred, agentKey());
        await expireNow(h, 'key_requests', expired);
        const overview = await ownerOverview(environment(h), id);
        assert.equal(overview.frozen, false);
        if (overview.frozen) return;
        assert.equal(overview.agents.length, 1);
        assert.deepEqual(overview.agents[0].keys.map(key => key.kid), [cred.key.kid]);
        assert.deepEqual(overview.agents[0].keyRequests.map(request => request.requestId), [pending]);
        assert.equal(overview.agents[0].keyRequests[0].endorsedBy, cred.key.kid);
    } finally { await h.close(); }
});

test('overview_FrozenHuman_FrozenOnly', async () => {
    const h = await start();
    try {
        await requested(h, '555');
        const id = await human(h, '555');
        await ownedAgent(h, id);
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(id).run();
        assert.deepEqual(await ownerOverview(environment(h), id), {frozen: true});
    } finally { await h.close(); }
});

test('confirmBinding_FrozenHuman_FrozenAndAgentOwnerless', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const id = await human(h, '555');
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(id).run();
        assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId}), {ok: false, reason: 'frozen'});
        assert.deepEqual(await act(h, id, {kind: 'reject-binding', requestId}), {ok: false, reason: 'frozen'});
        assert.equal(await ownerOf(h, cred.accountId), null);
        assert.equal(await bindingStatus(h, requestId), 'pending');
    } finally { await h.close(); }
});

test('confirmBinding_Fresh_SetsHumanIdAndConfirmed', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const id = await human(h, '555');
        assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId}), {ok: true});
        assert.equal(await ownerOf(h, cred.accountId), id);
        const view = await h.ok('/v1/binding-requests/' + requestId, cred);
        assert.equal(view.status, 'confirmed');
        assert.notEqual(view.decidedAt, null);
        const again = await h.request('/v1/binding-requests', cred, 'POST', {request: bindingRequest(cred.key, {accountId: cred.accountId, audience: bindingsUrl, human: 'github:555'})}) as unknown as Response;
        assert.equal(again.status, 409);
        const body = await again.json() as {reason: string};
        assert.equal(body.reason, 'owned-agent');
    } finally { await h.close(); }
});

test('confirmBinding_AuthAtBoundary_StaleRefusedAndNothingChanged', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const id = await human(h, '555'), now = Date.now();
        for (const authAt of [now - reauthWindowMs - 1, now + reauthFutureSkewMs + 1]) {
            assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId, authAt}, {clock: at(now)}), {ok: false, reason: 'stale-authentication'});
            assert.equal(await ownerOf(h, cred.accountId), null);
            assert.equal(await bindingStatus(h, requestId), 'pending');
        }
        assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId, authAt: now - reauthWindowMs}, {clock: at(now)}), {ok: true});
        assert.equal(await ownerOf(h, cred.accountId), id);
    } finally { await h.close(); }
});

test('confirmBinding_FutureSkewBoundary_Accepted', async () => {
    const h = await start();
    try {
        const {requestId} = await requested(h, '555');
        const now = Date.now();
        assert.deepEqual(await act(h, await human(h, '555'), {kind: 'confirm-binding', requestId, authAt: now + reauthFutureSkewMs}, {clock: at(now)}), {ok: true});
    } finally { await h.close(); }
});

test('confirmBinding_OtherHumansRequest_NotFound', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const other = await human(h, '556');
        assert.deepEqual(await act(h, other, {kind: 'confirm-binding', requestId}), {ok: false, reason: 'not-found'});
        assert.deepEqual(await act(h, other, {kind: 'reject-binding', requestId}), {ok: false, reason: 'not-found'});
        assert.equal(await ownerOf(h, cred.accountId), null);
        assert.equal(await bindingStatus(h, requestId), 'pending');
    } finally { await h.close(); }
});

test('confirmBinding_Twice_SecondNotPending', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const id = await human(h, '555');
        assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId}), {ok: true});
        assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId}), {ok: false, reason: 'not-pending'});
        assert.equal(await ownerOf(h, cred.accountId), id);
    } finally { await h.close(); }
});

test('confirmBinding_Expired_ExpiredButRejectStillRejects', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const id = await human(h, '555');
        await expireNow(h, 'binding_requests', requestId);
        assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId}), {ok: false, reason: 'expired'});
        assert.equal(await ownerOf(h, cred.accountId), null);
        assert.deepEqual(await act(h, id, {kind: 'reject-binding', requestId}), {ok: true});
        assert.equal(await bindingStatus(h, requestId), 'rejected');
    } finally { await h.close(); }
});

test('confirmBinding_AgentOwnedMeanwhile_AlreadyOwned', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const rival = await human(h, '556');
        await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(rival, cred.accountId).run();
        assert.deepEqual(await act(h, await human(h, '555'), {kind: 'confirm-binding', requestId}), {ok: false, reason: 'already-owned'});
        assert.equal(await ownerOf(h, cred.accountId), rival);
    } finally { await h.close(); }
});

test('confirmBinding_RequestingKeyRevoked_Refused', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        await revokeInStorage(h, cred.key.kid);
        assert.deepEqual(await act(h, await human(h, '555'), {kind: 'confirm-binding', requestId}), {ok: false, reason: 'requesting-key-revoked'});
        assert.equal(await ownerOf(h, cred.accountId), null);
    } finally { await h.close(); }
});

test('confirmBinding_KeyAddedAfterOverview_KeysChangedNoEffect', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const id = await human(h, '555');
        const seen = await ownerOverview(environment(h), id);
        assert.equal(seen.frozen, false);
        if (seen.frozen) return;
        const shown = seen.bindingRequests[0].keysDigest;
        assert.equal(shown, await liveDigest(h, cred.accountId));
        // The settled requesting key slips a new key in after the owner opened the page.
        const slipped = agentKey();
        const added = await h.request('/v1/keys', cred, 'POST', {proof: keyProof(slipped, {accountId: cred.accountId, audience: keysUrl}),
            endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: slipped.kid})}) as unknown as Response;
        assert.equal(added.status, 200, await added.clone().text());
        assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId, keysDigest: shown}), {ok: false, reason: 'keys-changed'});
        assert.equal(await ownerOf(h, cred.accountId), null);
        assert.equal(await bindingStatus(h, requestId), 'pending');
        // Positive control: the page reloaded with the current keys confirms.
        const reloaded = await ownerOverview(environment(h), id);
        assert.equal(reloaded.frozen, false);
        if (reloaded.frozen) return;
        const fresh = reloaded.bindingRequests[0].keysDigest;
        assert.notEqual(fresh, shown);
        assert.deepEqual(reloaded.bindingRequests[0].liveKeys.map(key => key.kid).sort(), [cred.key.kid, slipped.kid].sort());
        assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId, keysDigest: fresh}), {ok: true});
        assert.equal(await ownerOf(h, cred.accountId), id);
    } finally { await h.close(); }
});

test('confirmBinding_HumanAtTen_AgentLimitRequestStaysPending', async () => {
    const h = await start();
    try {
        const id = await human(h, '555');
        for (let i = 0; i < 10; i++) assert.equal((await createOwnedAgent(environment(h), {humanId: id, displayName: 'a' + i, sourceIp: '203.0.113.7'})).ok, true);
        const {cred, requestId} = await requested(h, '555');
        assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId}), {ok: false, reason: 'agent-limit'});
        assert.equal(await ownerOf(h, cred.accountId), null);
        assert.equal(await bindingStatus(h, requestId), 'pending');
    } finally { await h.close(); }
});

test('confirmBinding_TwoConcurrentAtNine_ExactlyOneOk', async () => {
    const h = await start();
    try {
        const id = await human(h, '555');
        for (let i = 0; i < 9; i++) assert.equal((await createOwnedAgent(environment(h), {humanId: id, displayName: 'a' + i, sourceIp: '203.0.113.7'})).ok, true);
        const first = await requested(h, '555'), second = await requested(h, '555');
        const results = await Promise.all([first, second].map(item => act(h, id, {kind: 'confirm-binding', requestId: item.requestId})));
        assert.deepEqual(results.map(result => result.ok ? 'ok' : (result as {reason: string}).reason).sort(), ['agent-limit', 'ok']);
        assert.equal(Number(await h.db.prepare('SELECT count(*) n FROM accounts WHERE human_id=?').bind(id).first('n')), 10);
    } finally { await h.close(); }
});

test('rejectBinding_Pending_Rejected_NoFreshnessNeeded', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const id = await human(h, '555');
        assert.deepEqual(await act(h, id, {kind: 'reject-binding', requestId, authAt: Date.now() - 86_400_000}), {ok: true});
        assert.equal(await bindingStatus(h, requestId), 'rejected');
        assert.equal(await ownerOf(h, cred.accountId), null);
        assert.equal((await h.ok('/v1/binding-requests/' + requestId, cred)).status, 'rejected');
        assert.deepEqual(await act(h, id, {kind: 'reject-binding', requestId}), {ok: false, reason: 'not-pending'});
    } finally { await h.close(); }
});

test('confirmKey_EndorsedRequest_KeyLiveSettledAndTokenIssues', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const id = await h.claim(cred), newKey = agentKey();
        const requestId = await addKey(h, cred, newKey);
        assert.deepEqual(await act(h, id, {kind: 'confirm-key', requestId}), {ok: true});
        assert.equal(await keyRequestStatus(h, requestId), 'confirmed');
        const row = await h.db.prepare('SELECT cooling_until,revoked_at FROM agent_keys WHERE kid=?').bind(newKey.kid).first<{cooling_until: number | null; revoked_at: number | null}>();
        assert.deepEqual(row, {cooling_until: null, revoked_at: null});
        assert.ok((await h.token({accountId: cred.accountId, key: newKey})).length > 0);
    } finally { await h.close(); }
});

test('confirmKey_StaleAuth_StaleAuthenticationAndRejectStillWorks', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const id = await h.claim(cred), requestId = await addKey(h, cred, agentKey());
        assert.deepEqual(await act(h, id, {kind: 'confirm-key', requestId, authAt: Date.now() - reauthWindowMs - 5000}), {ok: false, reason: 'stale-authentication'});
        assert.equal(await keyRequestStatus(h, requestId), 'pending');
        assert.deepEqual(await act(h, id, {kind: 'reject-key', requestId, authAt: Date.now() - 86_400_000}), {ok: true});
    } finally { await h.close(); }
});

test('confirmKey_EndorserRevokedMeanwhile_EndorserRevoked', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const id = await h.claim(cred), newKey = agentKey(), requestId = await addKey(h, cred, newKey);
        await revokeInStorage(h, cred.key.kid);
        assert.deepEqual(await act(h, id, {kind: 'confirm-key', requestId}), {ok: false, reason: 'endorser-revoked'});
        assert.equal(await keyRequestStatus(h, requestId), 'pending');
        assert.equal(Number(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE kid=?').bind(newKey.kid).first('n')), 0);
    } finally { await h.close(); }
});

test('confirmKey_ExpiredPending_ExpiredButRejectStillRejects', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const id = await h.claim(cred), requestId = await addKey(h, cred, agentKey());
        await expireNow(h, 'key_requests', requestId);
        assert.deepEqual(await act(h, id, {kind: 'confirm-key', requestId}), {ok: false, reason: 'expired'});
        assert.deepEqual(await act(h, id, {kind: 'reject-key', requestId}), {ok: true});
        assert.equal(await keyRequestStatus(h, requestId), 'rejected');
    } finally { await h.close(); }
});

test('confirmKey_OtherHumansAgent_NotFound', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        await h.claim(cred);
        const requestId = await addKey(h, cred, agentKey());
        const other = await h.claim(await h.register());
        assert.deepEqual(await act(h, other, {kind: 'confirm-key', requestId}), {ok: false, reason: 'not-found'});
        assert.deepEqual(await act(h, other, {kind: 'reject-key', requestId}), {ok: false, reason: 'not-found'});
        assert.equal(await keyRequestStatus(h, requestId), 'pending');
    } finally { await h.close(); }
});

test('confirmKey_SameKidPendingOnTwoOwnedAgents_SecondKeyInUse', async () => {
    const h = await start();
    try {
        const id = await human(h, '555');
        const first = await ownedAgent(h, id, 'one'), second = await ownedAgent(h, id, 'two'), shared = agentKey();
        const firstRequest = await addKey(h, first, shared), secondRequest = await addKey(h, second, shared);
        assert.deepEqual(await act(h, id, {kind: 'confirm-key', requestId: firstRequest}), {ok: true});
        assert.deepEqual(await act(h, id, {kind: 'confirm-key', requestId: secondRequest}), {ok: false, reason: 'key-in-use'});
        assert.equal(await keyRequestStatus(h, secondRequest), 'pending');
        assert.equal(await h.db.prepare('SELECT account_id FROM agent_keys WHERE kid=?').bind(shared.kid).first('account_id'), first.accountId);
    } finally { await h.close(); }
});

test('confirmKey_LiveKeyLimitOrChurnLimit_KeyLimit', async () => {
    const h = await start();
    try {
        const id = await human(h, '555'), old = BigInt(Date.now() - 3 * 86_400_000) * 10000n;
        const full = await ownedAgent(h, id, 'full'), fullRequest = await addKey(h, full, agentKey());
        for (let i = 0; i < 99; i++) await seedKey(h, full.accountId, {createdAt: old});
        assert.deepEqual(await act(h, id, {kind: 'confirm-key', requestId: fullRequest}), {ok: false, reason: 'key-limit'});
        const busy = await ownedAgent(h, id, 'busy'), busyRequest = await addKey(h, busy, agentKey());
        for (let i = 0; i < 10; i++) await seedKey(h, busy.accountId);
        assert.deepEqual(await act(h, id, {kind: 'confirm-key', requestId: busyRequest}), {ok: false, reason: 'key-limit'});
        assert.equal(await keyRequestStatus(h, busyRequest), 'pending');
    } finally { await h.close(); }
});

test('rejectKey_Pending_Rejected', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const id = await h.claim(cred), newKey = agentKey(), requestId = await addKey(h, cred, newKey);
        assert.deepEqual(await act(h, id, {kind: 'reject-key', requestId}), {ok: true});
        assert.equal(await keyRequestStatus(h, requestId), 'rejected');
        assert.equal(Number(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE kid=?').bind(newKey.kid).first('n')), 0);
        assert.deepEqual(await act(h, id, {kind: 'reject-key', requestId}), {ok: false, reason: 'not-pending'});
    } finally { await h.close(); }
});

test('revokeKey_LastLiveKey_RevokedAndItsTokenStopsWorking', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const id = await h.claim(cred);
        assert.equal(((await h.request('/v1/keys', cred)) as unknown as Response).status, 200);
        assert.deepEqual(await act(h, id, {kind: 'revoke-key', agentId: cred.accountId, kid: cred.key.kid}), {ok: true});
        assert.equal(((await h.request('/v1/keys', cred)) as unknown as Response).status, 401);
        assert.deepEqual(await act(h, id, {kind: 'revoke-key', agentId: cred.accountId, kid: cred.key.kid}), {ok: true});
    } finally { await h.close(); }
});

test('revokeKey_StaleAuthOrOtherHumansAgent_RefusedAndKeyStaysLive', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const id = await h.claim(cred), other = await h.claim(await h.register());
        assert.deepEqual(await act(h, id, {kind: 'revoke-key', agentId: cred.accountId, kid: cred.key.kid, authAt: Date.now() - reauthWindowMs - 5000}), {ok: false, reason: 'stale-authentication'});
        assert.deepEqual(await act(h, other, {kind: 'revoke-key', agentId: cred.accountId, kid: cred.key.kid}), {ok: false, reason: 'not-found'});
        assert.deepEqual(await act(h, id, {kind: 'revoke-key', agentId: crypto.randomUUID(), kid: cred.key.kid}), {ok: false, reason: 'not-found'});
        assert.equal(await h.db.prepare('SELECT revoked_at FROM agent_keys WHERE kid=?').bind(cred.key.kid).first('revoked_at'), null);
    } finally { await h.close(); }
});

test('issueRecoveryCode_FreshAuth_ReturnsCodeThatRedeems', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const id = await h.claim(cred), newKey = agentKey();
        const result = await act(h, id, {kind: 'issue-recovery-code', agentId: cred.accountId});
        assert.equal(result.ok, true);
        const {code, expiresAt} = result as {code: string; expiresAt: string};
        assert.match(code, /^[A-Za-z0-9_-]{43}$/);
        assert.ok(Date.parse(expiresAt) > Date.now());
        const redeemed = await h.mf.dispatchFetch(recoveryUrl, {method: 'POST', headers: {'Content-Type': 'application/json', DPoP: dpopProof(newKey, {method: 'POST', url: recoveryUrl})},
            body: JSON.stringify({agentId: cred.accountId, code})}) as unknown as Response;
        assert.equal(redeemed.status, 200, await redeemed.clone().text());
        assert.equal(((await redeemed.json()) as {kid: string}).kid, newKey.kid);
    } finally { await h.close(); }
});

test('issueRecoveryCode_StaleAuth_StaleAuthentication', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const id = await h.claim(cred);
        assert.deepEqual(await act(h, id, {kind: 'issue-recovery-code', agentId: cred.accountId, authAt: Date.now() - reauthWindowMs - 5000}), {ok: false, reason: 'stale-authentication'});
        assert.equal(Number(await h.db.prepare('SELECT count(*) n FROM recovery_codes').first('n')), 0);
    } finally { await h.close(); }
});

test('issueRecoveryCode_OtherHumansAgent_NotFound', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        await h.claim(cred);
        const other = await h.claim(await h.register());
        assert.deepEqual(await act(h, other, {kind: 'issue-recovery-code', agentId: cred.accountId}), {ok: false, reason: 'not-found'});
        assert.equal(Number(await h.db.prepare('SELECT count(*) n FROM recovery_codes').first('n')), 0);
    } finally { await h.close(); }
});

test('ownerAction_ReadOnlyOrMaintenance_UnavailableWithoutChange', async () => {
    const h = await start();
    try {
        const {cred, requestId} = await requested(h, '555');
        const id = await human(h, '555');
        for (const flag of ['MAINTENANCE_MODE', 'RECOVERY_READ_ONLY'] as const)
            assert.deepEqual(await act(h, id, {kind: 'confirm-binding', requestId}, {env: environment(h, {[flag]: 'true'})}), {ok: false, reason: 'unavailable'});
        assert.equal(await ownerOf(h, cred.accountId), null);
        assert.equal(await bindingStatus(h, requestId), 'pending');
    } finally { await h.close(); }
});

test('ownerAction_StorageFailure_UnavailableNotThrown', async () => {
    const h = await start();
    try {
        const {requestId} = await requested(h, '555');
        const id = await human(h, '555');
        const broken = {...environment(h), db: {prepare: () => { throw new Error('boom'); }, batch: () => { throw new Error('boom'); }}} as unknown as Env;
        assert.deepEqual(await act(h, id, {kind: 'reject-binding', requestId}, {env: broken}), {ok: false, reason: 'unavailable'});
    } finally { await h.close(); }
});

test('ownerOverview_Maintenance_Throws503', async () => {
    const h = await start();
    try {
        await assert.rejects(ownerOverview(environment(h, {MAINTENANCE_MODE: 'true'}), crypto.randomUUID()), (error: {status?: number}) => error.status === 503);
    } finally { await h.close(); }
});

test('ownerAction_MalformedInput_NotFound', async () => {
    const h = await start();
    try {
        const id = await human(h, '555');
        const valid = crypto.randomUUID(), kid = 'A'.repeat(43);
        const cases: unknown[] = [
            {humanId: id, authAt: Date.now(), kind: 'confirm-binding', requestId: 'not-a-uuid', keysDigest: kid},
            {humanId: id, authAt: Date.now(), kind: 'confirm-binding', requestId: valid},
            {humanId: id, authAt: Date.now(), kind: 'confirm-binding', requestId: valid, keysDigest: 'A'.repeat(42)},
            {humanId: id, authAt: Date.now(), kind: 'confirm-binding', requestId: valid, keysDigest: 'A'.repeat(42) + '='},
            {humanId: id, authAt: Date.now(), kind: 'reject-key', requestId: 42},
            {humanId: id, authAt: Date.now(), kind: 'revoke-key', agentId: valid, kid: 'A'.repeat(42)},
            {humanId: id, authAt: Date.now(), kind: 'revoke-key', agentId: 'x', kid},
            {humanId: id, authAt: Date.now(), kind: 'issue-recovery-code', agentId: 'x'},
            {humanId: id, authAt: String(Date.now()), kind: 'confirm-key', requestId: valid},
            {humanId: id, authAt: 1.5, kind: 'confirm-key', requestId: valid},
            {humanId: 'nope', authAt: Date.now(), kind: 'confirm-key', requestId: valid},
            {humanId: id, authAt: Date.now(), kind: 'transfer-agent', requestId: valid},
            null,
        ];
        for (const input of cases) assert.deepEqual(await ownerAction(environment(h), input as OwnerAction), {ok: false, reason: 'not-found'}, JSON.stringify(input));
    } finally { await h.close(); }
});
