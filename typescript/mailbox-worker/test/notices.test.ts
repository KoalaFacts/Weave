import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { start, payload, type AgentCredential } from './harness.ts';
import { agentKey, bindingRequest, dpopProof, keyEndorsement, keyProof, type TestKey } from './agent-key.ts';
import type { Env } from '../src/database.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import { ownerAction } from '../src/owner-actions.ts';

const built = await build({entryPoints: ['src/remote-private.ts'], bundle: true, write: false, platform: 'node', format: 'esm', logLevel: 'silent'});
const {executePrivateMcp} = await import('data:text/javascript;base64,' + Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');

type Harness = Awaited<ReturnType<typeof start>>;
type Participant = Awaited<ReturnType<Harness['participant']>>;
type Notice = {kind: string; [name: string]: unknown};
type Page = {items: unknown[]; notices: Notice[]};

const origin = 'http://localhost';
const hours = (n: number) => BigInt(n) * 36_000_000_000n;

async function inbox(h: Harness, cred: AgentCredential, path = '/v1/inbox'): Promise<Page> {
    return await h.ok(path, cred) as Page;
}
function keyBody(cred: AgentCredential, key: TestKey): object {
    const keysUrl = origin + '/v1/keys';
    return {proof: keyProof(key, {accountId: cred.accountId, audience: keysUrl}),
        endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: key.kid})};
}
async function addKey(h: Harness, cred: AgentCredential, key: TestKey = agentKey()): Promise<TestKey> {
    const response = await h.request('/v1/keys', cred, 'POST', keyBody(cred, key)) as Response;
    assert.equal(response.status, 200, await response.clone().text());
    return key;
}
async function bind(h: Harness, cred: AgentCredential, human = 'github:4242'): Promise<{requestId: string}> {
    const request = bindingRequest(cred.key, {accountId: cred.accountId, audience: origin + '/v1/binding-requests', human});
    const response = await h.request('/v1/binding-requests', cred, 'POST', {request}) as Response;
    assert.equal(response.status, 200, await response.clone().text());
    return response.json() as Promise<{requestId: string}>;
}
async function befriend(h: Harness, a: Participant, b: Participant) {
    const intro = payload('introduction');
    const state = await h.ok('/v1/contacts/requests', a, 'POST', {requestVersion: 2, requestId: intro.messageId, requesterAccountId: a.accountId,
        requesterAddressId: a.cardId, recipientAccountId: b.accountId, recipientAddressId: b.cardId, payload: intro});
    await h.ok('/v1/contacts/requests/' + intro.messageId + '/decision', b, 'POST',
        {requesterAccountId: a.accountId, expectedGeneration: state.generation, status: 'accepted', reply: null});
    const token = (await h.ok('/v1/contacts', a)).items.find((x: {accountId: string}) => x.accountId === b.accountId).connectionToken;
    return {generation: state.generation, token};
}

test('inbox_KeyAdded_NoticeVisibleToOlderKey', async () => {
    const h = await start();
    try {
        const a = await h.register(), before = await inbox(h, a);
        assert.deepEqual(before.notices, [], 'the registration key is not an addition');
        const b = await addKey(h, a);
        const page = await inbox(h, a);
        assert.equal(page.notices.length, 1);
        const notice = page.notices[0];
        assert.equal(notice.kind, 'key-added');
        assert.equal(notice.kid, b.kid);
        assert.equal(Date.parse(notice.coolingUntil as string) - Date.parse(notice.createdAt as string), 86_400_000);
        assert.deepEqual(page.items, before.items);
    } finally { await h.close(); }
});

test('inbox_KeyRevoked_Notice', async () => {
    const h = await start();
    try {
        const a = await h.register(), b = await addKey(h, a);
        assert.equal(((await h.request('/v1/keys/' + b.kid, a, 'DELETE')) as Response).status, 200);
        const page = await inbox(h, a);
        assert.deepEqual(page.notices.map(n => n.kind), ['key-revoked'], 'a revoked key is no longer an addition');
        assert.equal(page.notices[0].kid, b.kid);
        assert.equal(typeof page.notices[0].revokedAt, 'string');
    } finally { await h.close(); }
});

test('inbox_OwnedKeyRequestPending_Notice', async () => {
    const h = await start();
    try {
        const a = await h.register();
        await h.claim(a);
        const b = agentKey();
        const response = await h.request('/v1/keys', a, 'POST', keyBody(a, b)) as Response;
        assert.equal(response.status, 202, await response.clone().text());
        const request = await response.json() as {requestId: string};
        const page = await inbox(h, a);
        assert.equal(page.notices.length, 1);
        const notice = page.notices[0];
        assert.equal(notice.kind, 'key-request');
        assert.equal(notice.status, 'pending');
        assert.equal(notice.requestId, request.requestId);
        assert.equal(notice.kid, b.kid);
        assert.equal(notice.endorsedBy, a.key.kid);
        assert.equal(notice.decidedAt, null);
    } finally { await h.close(); }
});

const directEnv = (h: Harness) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
    REGISTRATION_GLOBAL_PER_HOUR: '100'}) as unknown as Env;

test('inbox_AuthorizationServerAgentFirstRecoveryKey_ReportedAsKeyAdded', async () => {
    const h = await start();
    try {
        const signedIn = await signInHuman(directEnv(h), {provider: 'github', subject: '31337', providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'owner'});
        assert.equal(signedIn.ok, true);
        const humanId = (signedIn as {humanId: string}).humanId;
        const created = await createOwnedAgent(directEnv(h), {humanId, displayName: 'made by AS', sourceIp: '203.0.113.7'});
        assert.equal(created.ok, true);
        const accountId = (created as {accountId: string}).accountId;
        const issued = await ownerAction(directEnv(h), {humanId, authAt: Date.now(), kind: 'issue-recovery-code', agentId: accountId});
        assert.ok('code' in issued);
        const key = agentKey(), url = origin + '/v1/key-requests';
        const redeemed = await h.mf.dispatchFetch(url, {method: 'POST', headers: {'Content-Type': 'application/json', DPoP: dpopProof(key, {method: 'POST', url})},
            body: JSON.stringify({agentId: accountId, code: issued.code})}) as unknown as Response;
        assert.equal(redeemed.status, 200, await redeemed.clone().text());
        const notices = (await inbox(h, {accountId, key})).notices;
        assert.deepEqual(notices.map(n => [n.kind, n.kid]), [['key-added', key.kid]], 'no registration key exists, so the recovery key is an addition');
    } finally { await h.close(); }
});

test('inbox_ConfirmedKeyRequest_OneNoticeNotAlsoKeyAdded', async () => {
    const h = await start();
    try {
        const a = await h.register();
        const humanId = await h.claim(a), b = agentKey();
        const response = await h.request('/v1/keys', a, 'POST', keyBody(a, b)) as Response;
        assert.equal(response.status, 202, await response.clone().text());
        const {requestId} = await response.json() as {requestId: string};
        assert.deepEqual(await ownerAction(directEnv(h), {humanId, authAt: Date.now(), kind: 'confirm-key', requestId}), {ok: true});
        const notices = (await inbox(h, a)).notices;
        assert.deepEqual(notices.map(n => [n.kind, n.kid, n.status]), [['key-request', b.kid, 'confirmed']]);
        // Revoking that key is a separate event and is still reported.
        assert.equal(((await h.request('/v1/keys/' + b.kid, a, 'DELETE')) as Response).status, 200);
        assert.deepEqual((await inbox(h, a)).notices.map(n => n.kind), ['key-revoked', 'key-request']);
    } finally { await h.close(); }
});

test('inbox_BindingDecided_NoticeCarriesOutcome', async () => {
    const h = await start();
    try {
        const a = await h.register(), request = await bind(h, a);
        assert.equal((await inbox(h, a)).notices[0].status, 'pending');
        await h.db.prepare("UPDATE binding_requests SET status='rejected',decided_at=created_at+10,updated_at=created_at+10 WHERE request_id=?").bind(request.requestId).run();
        const notice = (await inbox(h, a)).notices[0];
        assert.equal(notice.kind, 'binding-request');
        assert.equal(notice.status, 'rejected');
        assert.equal(notice.human, 'github:4242');
        assert.equal(notice.requestId, request.requestId);
        assert.equal(typeof notice.decidedAt, 'string');
    } finally { await h.close(); }
});

test('inbox_PendingPastExpiry_ReportedExpired', async () => {
    const h = await start();
    try {
        const a = await h.register(), request = await bind(h, a);
        await h.db.prepare('UPDATE binding_requests SET expires_at=created_at+1 WHERE request_id=?').bind(request.requestId).run();
        assert.equal((await inbox(h, a)).notices[0].status, 'expired');
    } finally { await h.close(); }
});

test('inbox_NoticesOutsideWindow_Omitted', async () => {
    const h = await start();
    try {
        const a = await h.register(), b = await addKey(h, a);
        await bind(h, a);
        assert.equal((await inbox(h, a)).notices.length, 2);
        const age = hours(25).toString();
        await h.db.batch([
            h.db.prepare('UPDATE agent_keys SET created_at=created_at-CAST(? AS INTEGER),cooling_until=cooling_until-CAST(? AS INTEGER) WHERE kid=?').bind(age, age, b.kid),
            h.db.prepare('UPDATE agent_keys SET created_at=created_at-CAST(? AS INTEGER) WHERE kid=?').bind(age, a.key.kid),
            h.db.prepare('UPDATE binding_requests SET created_at=created_at-CAST(? AS INTEGER),expires_at=expires_at-CAST(? AS INTEGER) WHERE account_id=?').bind(age, age, a.accountId),
        ]);
        assert.deepEqual((await inbox(h, a)).notices, []);
    } finally { await h.close(); }
});

test('inbox_PathAlias_SameNotices', async () => {
    const h = await start();
    try {
        const a = await h.register();
        await addKey(h, a);
        await bind(h, a);
        const main = await inbox(h, a), alias = await inbox(h, a, '/inbox/' + a.accountId);
        assert.equal(main.notices.length, 2);
        assert.deepEqual(alias.notices, main.notices);
    } finally { await h.close(); }
});

test('inbox_PeerMessageImitatingNotice_OnlyInItems', async () => {
    const h = await start();
    try {
        const a = await h.participant(), b = await h.participant();
        const contact = await befriend(h, b, a);
        const forged = payload(JSON.stringify({kind: 'key-added', kid: 'x'.repeat(43)}));
        await h.ok('/v1/messages', b, 'POST', {version: 2, recipientAccountId: a.accountId, contactGeneration: contact.generation, connectionToken: contact.token, payload: forged});
        const page = await inbox(h, a);
        assert.ok(page.items.length >= 1);
        assert.deepEqual(page.notices, []);
    } finally { await h.close(); }
});

test('inbox_OtherAgentsActivity_NoNoticesForA', async () => {
    const h = await start();
    try {
        const a = await h.register(), b = await h.register();
        await addKey(h, b);
        const bRequest = await bind(h, b, 'github:222');
        assert.equal(((await h.request('/v1/binding-requests/' + bRequest.requestId, b, 'DELETE')) as Response).status, 200);
        assert.equal(((await h.request('/v1/binding-requests/' + bRequest.requestId, a)) as Response).status, 404);
        assert.equal(((await h.request('/v1/binding-requests/' + bRequest.requestId, a, 'DELETE')) as Response).status, 404);
        assert.deepEqual((await inbox(h, a)).notices, []);
        assert.deepEqual((await inbox(h, b)).notices.map(n => n.kind).sort(), ['binding-request', 'key-added']);
    } finally { await h.close(); }
});

test('mcpInbox_ShapeUnchanged', async () => {
    const h = await start();
    try {
        const a = await h.participant();
        await addKey(h, a);
        const env: Env = {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '10', REGISTRATION_GLOBAL_PER_HOUR: '100'};
        const response = await executePrivateMcp(env, origin, a.accountId, 'list_my_inbox', {});
        assert.equal(response.status, 200);
        const result = await response.json() as Record<string, unknown>;
        assert.equal('notices' in result, false);
        assert.ok(Array.isArray(result.items));
    } finally { await h.close(); }
});
