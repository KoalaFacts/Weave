import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import { maxNotices } from '../src/notices.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Notice = {kind: string; [name: string]: unknown};
type Provider = {calls: number; respond: () => Response};

const endpoint = 'https://provider.test/post';
const hourTicks = 36_000_000_000n;
const nowTicks = () => BigInt(Date.now()) * 10000n;

async function scenario(run: (s: {h: Harness; provider: Provider}) => Promise<void>) {
    const provider: Provider = {calls: 0, respond: () => Response.json({externalId: 'post-1'})};
    const h = await start({bindings: {EXTERNAL_ACTION_TEST_ENDPOINT: endpoint}, outboundService: async (request: Request) => {
        if (request.url !== endpoint) return new Response('unexpected', {status: 599});
        provider.calls++;
        return provider.respond();
    }});
    try { await run({h, provider}); } finally { await h.close(); }
}

const environment = (h: Harness) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
    REGISTRATION_GLOBAL_PER_HOUR: '100', EXTERNAL_ACTION_TEST_ENDPOINT: endpoint}) as unknown as Env;

async function ownedAgent(h: Harness) {
    const agent = await h.register();
    const humanId = await h.claim(agent);
    const at = nowTicks().toString();
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
        VALUES(?,'linkedin-main','test-http','rev-1','urn:li:person:alpha','Desk Alpha',CAST(? AS INTEGER),CAST(? AS INTEGER))`).bind(agent.accountId, at, at).run();
    return {agent, humanId};
}

async function propose(h: Harness, agent: AgentCredential, content = 'Episode 12 is live.'): Promise<{actionId: string; createdAt: string; expiresAt: string}> {
    const response = await h.request('/v1/external-actions', agent, 'POST', {credentialRef: 'linkedin-main', operation: 'publish_text', content}) as Response;
    assert.equal(response.status, 200, await response.clone().text());
    return response.json() as Promise<{actionId: string; createdAt: string; expiresAt: string}>;
}

const decide = (h: Harness, humanId: string, action: Record<string, unknown>) =>
    ownerAction(environment(h), {humanId, authAt: Date.now(), ...action} as OwnerAction);

async function approve(h: Harness, humanId: string, actionId: string) {
    const overview = await ownerOverview(environment(h), humanId) as {externalActions: {actionId: string; planDigest: string}[]};
    const shown = overview.externalActions.find(action => action.actionId === actionId);
    assert.ok(shown);
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: actionId, planDigest: shown.planDigest}), {ok: true});
}

async function actionNotices(h: Harness, agent: AgentCredential): Promise<Notice[]> {
    const page = await h.ok('/v1/inbox', agent) as {notices: Notice[]};
    return page.notices.filter(notice => notice.kind === 'external-action');
}

test('inbox_ProposedAction_PendingNoticeWithoutContent', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const action = await propose(h, agent);
    const notices = await actionNotices(h, agent);
    assert.deepEqual(notices, [{kind: 'external-action', actionId: action.actionId, status: 'pending', credentialRef: 'linkedin-main',
        operation: 'publish_text', createdAt: action.createdAt, expiresAt: action.expiresAt, decidedAt: null, completedAt: null}]);
    assert.equal(JSON.stringify(notices).includes('Episode 12'), false, 'notices never carry the planned content');
}));

test('inbox_ApprovedAndExecuted_OneNoticeWithLatestStatus', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const {actionId} = await propose(h, agent);
    await approve(h, humanId, actionId);
    const approved = await actionNotices(h, agent);
    assert.deepEqual(approved.map(n => [n.actionId, n.status]), [[actionId, 'approved']]);
    assert.equal(typeof approved[0].decidedAt, 'string');

    const executed = await h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {}) as {status: string; completedAt: string};
    assert.equal(executed.status, 'completed');
    assert.equal(provider.calls, 1);
    const completed = await actionNotices(h, agent);
    assert.deepEqual(completed.map(n => [n.actionId, n.status, n.completedAt]), [[actionId, 'completed', executed.completedAt]]);
}));

test('inbox_UnknownOutcome_NoticeReportsUnknown', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const {actionId} = await propose(h, agent);
    await approve(h, humanId, actionId);
    provider.respond = () => new Response('{}', {status: 502});
    assert.equal((await h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {}) as {status: string}).status, 'unknown');
    assert.deepEqual((await actionNotices(h, agent)).map(n => n.status), ['unknown']);
}));

test('inbox_RejectedAction_NoticeReportsRejected', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const {actionId} = await propose(h, agent);
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: actionId}), {ok: true});
    const notices = await actionNotices(h, agent);
    assert.deepEqual(notices.map(n => [n.actionId, n.status]), [[actionId, 'rejected']]);
    assert.equal(typeof notices[0].decidedAt, 'string');
}));

test('inbox_PendingOrApprovedPastExpiry_NoticeReportsExpired', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const pending = await propose(h, agent, 'first');
    const approved = await propose(h, agent, 'second');
    await approve(h, humanId, approved.actionId);
    const past = (nowTicks() - 1n).toString();
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE account_id=?').bind(past, agent.accountId).run();
    const notices = await actionNotices(h, agent);
    assert.deepEqual(notices.map(n => n.status), ['expired', 'expired']);
    assert.deepEqual(new Set(notices.map(n => n.actionId)), new Set([pending.actionId, approved.actionId]));
}));

test('inbox_ExpiryAfterLastUpdate_NewestFirstByExpiry', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const older = await propose(h, agent, 'first');
    const newer = await propose(h, agent, 'second');
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: newer.actionId}), {ok: true});
    // The pending one expired after the other was rejected, so its expiry is the newer event.
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind((nowTicks() - 1n).toString(), older.actionId).run();
    assert.deepEqual((await actionNotices(h, agent)).map(n => [n.actionId, n.status]), [[older.actionId, 'expired'], [newer.actionId, 'rejected']]);
}));

test('inbox_ActionOutsideWindow_NotReported', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const kept = await propose(h, agent, 'kept');
    const old = await propose(h, agent, 'old');
    const created = nowTicks() - 30n * hourTicks;
    await h.db.prepare(`UPDATE external_actions SET created_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER),expires_at=CAST(? AS INTEGER)
        WHERE action_id=?`).bind(created.toString(), created.toString(), (created + hourTicks).toString(), old.actionId).run();
    assert.deepEqual((await actionNotices(h, agent)).map(n => n.actionId), [kept.actionId], 'an action that changed and expired over 24 hours ago is not reported');
}));

test('inbox_AnotherAgentsAction_NotReported', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const {agent: other} = await ownedAgent(h);
    const mine = await propose(h, agent);
    await propose(h, other);
    assert.deepEqual((await actionNotices(h, agent)).map(n => n.actionId), [mine.actionId]);
}));

test('inbox_ManyActions_CappedAtMaxNoticesNewestFirst', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const template = await propose(h, agent);
    const base = nowTicks() - hourTicks;
    const ids: string[] = [];
    for (let i = 0; i < maxNotices + 5; i++) {
        const id = crypto.randomUUID(), at = (base + BigInt(i) * 10_000_000n).toString();
        ids.push(id);
        await h.db.prepare(`INSERT INTO external_actions(action_id,account_id,provider,contract_revision,operation,content,credential_ref,
            credential_revision,target_account,plan_digest,status,created_at,expires_at,updated_at,decided_at,decided_by)
            SELECT ?,account_id,provider,contract_revision,operation,content,credential_ref,credential_revision,target_account,plan_digest,
            'rejected',CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER),? FROM external_actions WHERE action_id=?`)
            .bind(id, at, (BigInt(at) + hourTicks).toString(), at, at, humanId, template.actionId).run();
    }
    const notices = (await h.ok('/v1/inbox', agent) as {notices: Notice[]}).notices;
    assert.equal(notices.length, maxNotices);
    assert.equal(notices[0].actionId, template.actionId, 'the newest change comes first');
    assert.deepEqual(notices.slice(1).map(n => n.actionId), ids.slice(-(maxNotices - 1)).reverse());
}));

test('inbox_ExpiredInWindowAfterOlderLastChange_StillReported', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const {actionId} = await propose(h, agent);
    // Proposed 24.5 hours ago and never touched since; it lapsed 23.5 hours ago, inside the window.
    const created = nowTicks() - 24n * hourTicks - hourTicks / 2n;
    await h.db.prepare(`UPDATE external_actions SET created_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER),expires_at=CAST(? AS INTEGER)
        WHERE action_id=?`).bind(created.toString(), created.toString(), (created + hourTicks).toString(), actionId).run();
    assert.deepEqual((await actionNotices(h, agent)).map(n => [n.actionId, n.status]), [[actionId, 'expired']]);
}));

test('inbox_ProviderRejected_NoticeReportsFailed', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const {actionId} = await propose(h, agent);
    await approve(h, humanId, actionId);
    provider.respond = () => Response.json({reason: 'duplicate content'}, {status: 422});
    assert.equal((await h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {}) as {status: string}).status, 'failed');
    assert.deepEqual((await actionNotices(h, agent)).map(n => [n.actionId, n.status]), [[actionId, 'failed']]);
}));

test('inbox_LapsedPlanNewerThanCappedRows_RankedByExpiry', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const lapsed = await propose(h, agent, 'lapsed');
    const template = await propose(h, agent, 'template');
    const now = nowTicks();
    // Last written 90 minutes ago, before every row below, but it lapsed 30 minutes ago, after all of them.
    const written = now - hourTicks - hourTicks / 2n;
    await h.db.prepare(`UPDATE external_actions SET created_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER),expires_at=CAST(? AS INTEGER)
        WHERE action_id=?`).bind(written.toString(), written.toString(), (written + hourTicks).toString(), lapsed.actionId).run();
    const base = now - hourTicks;
    for (let i = 0; i < maxNotices + 5; i++) {
        const at = (base + BigInt(i) * 10_000_000n).toString();
        await h.db.prepare(`INSERT INTO external_actions(action_id,account_id,provider,contract_revision,operation,content,credential_ref,
            credential_revision,target_account,plan_digest,status,created_at,expires_at,updated_at,decided_at,decided_by)
            SELECT ?,account_id,provider,contract_revision,operation,content,credential_ref,credential_revision,target_account,plan_digest,
            'rejected',CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER),? FROM external_actions WHERE action_id=?`)
            .bind(crypto.randomUUID(), at, (BigInt(at) + hourTicks).toString(), at, at, humanId, template.actionId).run();
    }
    const notices = (await h.ok('/v1/inbox', agent) as {notices: Notice[]}).notices;
    assert.equal(notices.length, maxNotices);
    const position = notices.findIndex(n => n.actionId === lapsed.actionId);
    assert.ok(position >= 0, 'the lapsed plan is among the newest 50 by its expiry');
    assert.equal(notices[position].status, 'expired');
    assert.equal(notices[1].actionId, lapsed.actionId, 'only the just-proposed template is newer');
}));
