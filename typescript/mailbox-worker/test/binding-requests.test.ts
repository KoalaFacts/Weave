import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { agentKey, bindingRequest, keyEndorsement, keyProof } from './agent-key.ts';
import { signInHuman } from '../src/humans.ts';
import { createBindingRequest } from '../src/binding-requests.ts';
import { Problem } from '../src/wire.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type View = {requestId: string; agentId: string; human: string; kid: string; status: string; createdAt: string; expiresAt: string; decidedAt: string | null};
type Overrides = {iat?: number; header?: object; claims?: object; signer?: AgentCredential['key']; audience?: string; accountId?: string};

const origin = 'http://localhost';
const audience = origin + '/v1/binding-requests';

function requestBinding(h: Harness, cred: AgentCredential, human: string, overrides: Overrides = {}): Promise<Response> {
    const request = bindingRequest(overrides.signer ?? cred.key, {accountId: overrides.accountId ?? cred.accountId, audience: overrides.audience ?? audience, human,
        ...(overrides.iat === undefined ? {} : {iat: overrides.iat}), header: {kid: cred.key.kid, ...overrides.header}, claims: overrides.claims});
    return h.request('/v1/binding-requests', cred, 'POST', {request}) as unknown as Promise<Response>;
}
async function created(h: Harness, cred: AgentCredential, human: string): Promise<View> {
    const response = await requestBinding(h, cred, human);
    assert.equal(response.status, 200, await response.clone().text());
    return response.json() as Promise<View>;
}
async function problem(response: Response, status: number, expected: Record<string, unknown>): Promise<void> {
    const text = await response.text();
    assert.equal(response.status, status, text);
    const value = JSON.parse(text) as Record<string, unknown>;
    for (const [name, wanted] of Object.entries(expected)) assert.equal(value[name], wanted, text);
}
const rows = async (h: Harness, accountId: string) => Number(await h.db.prepare('SELECT count(*) n FROM binding_requests WHERE account_id=?').bind(accountId).first('n'));
const expireNow = (h: Harness, requestId: string) => h.db.prepare('UPDATE binding_requests SET expires_at=created_at+1 WHERE request_id=?').bind(requestId).run();
const cancel = (h: Harness, cred: AgentCredential, requestId: string) => h.request('/v1/binding-requests/' + requestId, cred, 'DELETE') as unknown as Promise<Response>;
const read = (h: Harness, cred: AgentCredential, requestId: string) => h.request('/v1/binding-requests/' + requestId, cred) as unknown as Promise<Response>;
function env(h: Harness): Env {
    return {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100'} as unknown as Env;
}
async function human(h: Harness, subject: string): Promise<string> {
    const result = await signInHuman(env(h), {provider: 'github', subject, providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'person' + subject});
    assert.equal(result.ok, true);
    return (result as {humanId: string}).humanId;
}

test('createBinding_SignedRequest_PendingFor24h', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const view = await created(h, cred, 'github:4242');
        assert.equal(view.status, 'pending');
        assert.equal(view.human, 'github:4242');
        assert.equal(view.agentId, cred.accountId);
        assert.equal(view.kid, cred.key.kid);
        assert.match(view.requestId, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
        assert.equal(Date.parse(view.expiresAt) - Date.parse(view.createdAt), 86_400_000);
        assert.equal(view.decidedAt, null);
    } finally { await h.close(); }
});

test('createBinding_SameHumanRetry_ReturnsSameRequest', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        const first = await created(h, cred, 'github:4242'), second = await created(h, cred, 'github:4242');
        assert.deepEqual(second, first);
        assert.equal(await rows(h, cred.accountId), 1);
    } finally { await h.close(); }
});

test('createBinding_OtherHumanWhilePending_409BindingPending', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        await created(h, cred, 'github:4242');
        await problem(await requestBinding(h, cred, 'github:4243'), 409, {code: 'conflict', field: 'request', reason: 'binding-pending'});
        assert.equal(await rows(h, cred.accountId), 1);
    } finally { await h.close(); }
});

test('createBinding_UnknownFrozenAndActiveHumans_IdenticalShape', async () => {
    const h = await start();
    try {
        const frozen = await human(h, '5001');
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(frozen).run();
        await human(h, '5002');
        const views: View[] = [];
        for (const subject of ['5003', '5001', '5002']) views.push(await created(h, await h.register(), 'github:' + subject));
        for (const view of views) {
            assert.equal(view.status, 'pending');
            assert.deepEqual(Object.keys(view).sort(), Object.keys(views[0]).sort());
            assert.equal(view.decidedAt, null);
        }
    } finally { await h.close(); }
});

test('createBinding_JwsSignedByOtherKey_400', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        await problem(await requestBinding(h, cred, 'github:4242', {signer: agentKey()}), 400, {code: 'invalid', field: 'request'});
        assert.equal(await rows(h, cred.accountId), 0);
    } finally { await h.close(); }
});

test('createBinding_HeaderKidNotCaller_400', async () => {
    const h = await start();
    try {
        const cred = await h.register(), other = agentKey();
        await problem(await requestBinding(h, cred, 'github:4242', {signer: other, header: {kid: other.kid}}), 400, {code: 'invalid', field: 'request'});
        await problem(await requestBinding(h, cred, 'github:4242', {header: {kid: other.kid}}), 400, {code: 'invalid', field: 'request'});
        await problem(await requestBinding(h, cred, 'github:4242', {header: {typ: 'weave-key-proof+jwt'}}), 400, {code: 'invalid', field: 'request'});
        assert.equal(await rows(h, cred.accountId), 0);
    } finally { await h.close(); }
});

test('createBinding_WrongAudOrSubOrStaleIat_400', async () => {
    const h = await start();
    try {
        const cred = await h.register(), other = await h.register();
        for (const overrides of [{audience: origin + '/v1/keys'}, {accountId: other.accountId}] as Overrides[])
            await problem(await requestBinding(h, cred, 'github:4242', overrides), 400, {code: 'invalid', field: 'request'});
        // iat skew against a fixed clock, so the ±60 s boundary cannot drift with the test's own run time.
        const now = Math.floor(Date.now() / 1000), clock = () => BigInt(now) * 10000000n;
        const direct = (iat: number) => createBindingRequest(env(h), new Request(audience, {method: 'POST'}), {accountId: cred.accountId, kid: cred.key.kid},
            {request: bindingRequest(cred.key, {accountId: cred.accountId, audience, human: 'github:4242', iat})}, clock);
        for (const iat of [now - 61, now + 61])
            await assert.rejects(direct(iat), (error: unknown) => error instanceof Problem && error.status === 400 && error.detail?.field === 'request', 'iat ' + (iat - now));
        const malformed = await h.request('/v1/binding-requests', cred, 'POST', {request: 'not.a.jws'});
        await problem(malformed as unknown as Response, 400, {code: 'invalid', field: 'request'});
        const extra = await h.request('/v1/binding-requests', cred, 'POST', {request: bindingRequest(cred.key, {accountId: cred.accountId, audience, human: 'github:1'}), human: 'github:2'});
        await problem(extra as unknown as Response, 400, {code: 'invalid'});
        assert.equal(await rows(h, cred.accountId), 0);
    } finally { await h.close(); }
});

test('createBinding_BadHumanFormat_400', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        for (const target of ['github:0', 'github:abc', 'gitlab:1', 'github:01', 'github:' + '1'.repeat(21)])
            await problem(await requestBinding(h, cred, target), 400, {code: 'invalid', field: 'request'});
        assert.equal(await rows(h, cred.accountId), 0);
    } finally { await h.close(); }
});

test('createBinding_CoolingKey_409Cooling', async () => {
    const h = await start();
    try {
        const cred = await h.register(), newKey = agentKey(), keysUrl = origin + '/v1/keys';
        const added = await h.request('/v1/keys', cred, 'POST', {proof: keyProof(newKey, {accountId: cred.accountId, audience: keysUrl}),
            endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: newKey.kid})});
        assert.equal(added.status, 200, await added.clone().text());
        await problem(await requestBinding(h, {accountId: cred.accountId, key: newKey}, 'github:4242'), 409, {code: 'conflict', field: 'request', reason: 'cooling'});
        assert.equal(await rows(h, cred.accountId), 0);
    } finally { await h.close(); }
});

test('createBinding_OwnedAgent_409OwnedAgent', async () => {
    const h = await start();
    try {
        const cred = await h.register(), humanId = await human(h, '123');
        await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(humanId, cred.accountId).run();
        await problem(await requestBinding(h, cred, 'github:123'), 409, {code: 'conflict', field: 'request', reason: 'owned-agent'});
        assert.equal(await rows(h, cred.accountId), 0);
    } finally { await h.close(); }
});

test('createBinding_SixthInADay_429', async () => {
    const h = await start();
    try {
        const cred = await h.register();
        for (let i = 0; i < 5; i++) {
            const view = await created(h, cred, 'github:4242');
            assert.equal((await cancel(h, cred, view.requestId)).status, 200);
        }
        await problem(await requestBinding(h, cred, 'github:4242'), 429, {code: 'capacity'});
        assert.equal(await rows(h, cred.accountId), 5);
    } finally { await h.close(); }
});

test('createBinding_TwentyFirstPendingForOneHuman_429', async () => {
    const h = await start({bindings: {REGISTRATION_PER_IP_PER_HOUR: '100'}});
    try {
        for (let i = 0; i < 20; i++) await created(h, await h.register(), 'github:7');
        const late = await h.register();
        await problem(await requestBinding(h, late, 'github:7'), 429, {code: 'capacity'});
        assert.equal(await rows(h, late.accountId), 0);
        assert.equal((await created(h, late, 'github:8')).status, 'pending');
    } finally { await h.close(); }
});

test('createBinding_ExpiredPending_NewRequestAllowed', async () => {
    const h = await start();
    try {
        const cred = await h.register(), old = await created(h, cred, 'github:4242');
        await expireNow(h, old.requestId);
        const fresh = await created(h, cred, 'github:4243');
        assert.notEqual(fresh.requestId, old.requestId);
        assert.equal(fresh.status, 'pending');
        const response = await read(h, cred, old.requestId);
        assert.equal(response.status, 200);
        const stale = await response.json() as View;
        assert.equal(stale.status, 'expired');
        assert.notEqual(stale.decidedAt, null);
        assert.equal(await h.db.prepare('SELECT status FROM binding_requests WHERE request_id=?').bind(old.requestId).first('status'), 'expired');
    } finally { await h.close(); }
});

test('getBinding_ExpiredPending_ReportsExpired', async () => {
    const h = await start();
    try {
        const cred = await h.register(), view = await created(h, cred, 'github:4242');
        const fresh = await read(h, cred, view.requestId);
        assert.equal(fresh.status, 200);
        assert.deepEqual(await fresh.json(), view);
        await expireNow(h, view.requestId);
        const response = await read(h, cred, view.requestId);
        assert.equal(response.status, 200);
        const expired = await response.json() as View;
        assert.equal(expired.status, 'expired');
        assert.equal(expired.decidedAt, null);
    } finally { await h.close(); }
});

test('getBinding_OtherAgentsRequest_404', async () => {
    const h = await start();
    try {
        const cred = await h.register(), other = await h.register(), view = await created(h, cred, 'github:4242');
        await problem(await read(h, other, view.requestId), 404, {code: 'unavailable'});
        await problem(await read(h, cred, crypto.randomUUID()), 404, {code: 'unavailable'});
        await problem(await read(h, cred, view.requestId.toUpperCase()), 404, {code: 'unavailable'});
        await problem(await h.request('/v1/binding-requests/' + view.requestId, null) as unknown as Response, 401, {code: 'unauthenticated'});
    } finally { await h.close(); }
});

test('cancelBinding_Pending_Cancelled', async () => {
    const h = await start();
    try {
        const cred = await h.register(), view = await created(h, cred, 'github:4242');
        const response = await cancel(h, cred, view.requestId);
        assert.equal(response.status, 200, await response.clone().text());
        const cancelled = await response.json() as View;
        assert.equal(cancelled.status, 'cancelled');
        assert.equal(cancelled.requestId, view.requestId);
        assert.notEqual(cancelled.decidedAt, null);
        assert.equal(await h.db.prepare('SELECT status FROM binding_requests WHERE request_id=?').bind(view.requestId).first('status'), 'cancelled');
        assert.equal((await created(h, cred, 'github:4243')).status, 'pending');
    } finally { await h.close(); }
});

test('cancelBinding_Repeat_ReturnsCancelledUnchanged', async () => {
    const h = await start();
    try {
        const cred = await h.register(), view = await created(h, cred, 'github:4242');
        const first = await (await cancel(h, cred, view.requestId)).json();
        const again = await cancel(h, cred, view.requestId);
        assert.equal(again.status, 200);
        assert.deepEqual(await again.json(), first);
        const expiring = await h.register(), stale = await created(h, expiring, 'github:4242');
        await expireNow(h, stale.requestId);
        const expired = await cancel(h, expiring, stale.requestId);
        assert.equal(expired.status, 200);
        assert.equal((await expired.json() as View).status, 'expired');
        assert.equal(await h.db.prepare('SELECT status FROM binding_requests WHERE request_id=?').bind(stale.requestId).first('status'), 'pending');
    } finally { await h.close(); }
});

test('cancelBinding_OtherAgent_404', async () => {
    const h = await start();
    try {
        const cred = await h.register(), other = await h.register(), view = await created(h, cred, 'github:4242');
        await problem(await cancel(h, other, view.requestId), 404, {code: 'unavailable'});
        assert.equal(await h.db.prepare('SELECT status FROM binding_requests WHERE request_id=?').bind(view.requestId).first('status'), 'pending');
    } finally { await h.close(); }
});

test('createBinding_OldDecidedRows_PrunedExceptConfirmed', async () => {
    const h = await start();
    try {
        const cred = await h.register(), old = (BigInt(Date.now()) - 31n * 86_400_000n) * 10000n;
        const insert = (requestId: string, status: string) => h.db.prepare(`INSERT INTO binding_requests(request_id,account_id,target_subject,kid,status,created_at,expires_at,updated_at,decided_at)
            VALUES(?,?,'4242',?,?,CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER))`)
            .bind(requestId, cred.accountId, cred.key.kid, status, old.toString(), (old + 864000000000n).toString(), old.toString(), (old + 1n).toString()).run();
        for (const status of ['confirmed', 'rejected', 'cancelled', 'expired']) await insert('old-' + status, status);
        await created(h, await h.register(), 'github:4242');
        const left = (await h.db.prepare('SELECT request_id FROM binding_requests WHERE request_id LIKE ?').bind('old-%').all<{request_id: string}>()).results.map((r: {request_id: string}) => r.request_id);
        // A confirmed request is the consent record behind accounts.human_id and is kept.
        assert.deepEqual(left, ['old-confirmed']);
    } finally { await h.close(); }
});
