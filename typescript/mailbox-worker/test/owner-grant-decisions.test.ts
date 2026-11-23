import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import { executeOwnerMcp } from '../src/owner-tools.ts';
import { type Env } from '../src/database.ts';
import { ownerPendingAction } from '../src/external-actions.ts';

// A1-2 step 2: an app holding an owner grant approves or rejects its human's agents' pending plans through the owner-grant
// tools, only while the owner lets it, with the owner page's own rules, and the grant is recorded on each decision.

type Harness = Awaited<ReturnType<typeof start>>;
type Call = {actionId: string};
const endpoint = 'https://provider.test/post';

async function scenario(run: (s: {h: Harness; calls: Call[]}) => Promise<void>) {
    const calls: Call[] = [];
    const h = await start({bindings: {EXTERNAL_ACTION_TEST_ENDPOINT: endpoint}, outboundService: async (request: Request) => {
        if (request.url !== endpoint) return new Response('unexpected', {status: 599});
        const call = await request.json() as Call;
        calls.push(call);
        return Response.json({externalId: 'post-' + call.actionId});
    }});
    try { await run({h, calls}); } finally { await h.close(); }
}

const environment = (h: Harness, overrides: Partial<Env> = {}) => ({db: h.db, EXTERNAL_ACTION_TEST_ENDPOINT: endpoint, ...overrides}) as unknown as Env;
const ticks = () => (BigInt(Date.now()) * 10000n).toString();

async function setCredential(h: Harness, accountId: string, revision = 'rev-1') {
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
        VALUES(?,'linkedin-main','test-http',?,'urn:li:person:alpha','Desk Alpha',CAST(? AS INTEGER),CAST(? AS INTEGER))
        ON CONFLICT(account_id,credential_ref) DO UPDATE SET revision=excluded.revision,updated_at=excluded.updated_at`)
        .bind(accountId, revision, ticks(), ticks()).run();
}

async function ownedAgent(h: Harness) {
    const agent = await h.register();
    const humanId = await h.claim(agent);
    await setCredential(h, agent.accountId);
    return {agent, humanId};
}

/** An owner grant of the human; `decides` adds the owner's consent-page choice to let the app approve or reject. */
async function grant(h: Harness, humanId: string, decides = true) {
    const grantId = crypto.randomUUID(), at = ticks();
    await h.db.prepare('INSERT INTO owner_grants(grant_id,human_id,client_id,created_at,revoked_at) VALUES(?,?,?,CAST(? AS INTEGER),NULL)')
        .bind(grantId, humanId, 'https://office.example/client', at).run();
    if (decides) await h.db.prepare('INSERT INTO owner_grant_approvals(grant_id,human_id,created_at,revoked_at) VALUES(?,?,CAST(? AS INTEGER),NULL)')
        .bind(grantId, humanId, at).run();
    return grantId;
}

const propose = async (h: Harness, as: AgentCredential) => {
    const response = await h.request('/v1/external-actions', as, 'POST', {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Episode 12 is live.'});
    assert.equal(response.status, 200, await response.clone().text());
    const body = await response.json() as {actionId: string};
    return body.actionId;
};
const digestOf = async (h: Harness, humanId: string, actionId: string) => {
    const overview = await ownerOverview(environment(h), humanId) as {externalActions: {actionId: string; planDigest: string}[]};
    return overview.externalActions.find(a => a.actionId === actionId)!.planDigest;
};
const tool = async (h: Harness, humanId: string, grantId: string, name: string, args: Record<string, unknown>, env = environment(h)) => {
    const response = await executeOwnerMcp(env, humanId, name, args, grantId, '198.51.100.4');
    return {status: response.status, body: await response.json() as Record<string, unknown>};
};
const codeOf = (result: {body: Record<string, unknown>}) => result.body.code ?? result.body.error ?? result.body.type;
const row = (h: Harness, actionId: string) => h.db.prepare('SELECT status,decided_by FROM external_actions WHERE action_id=?').bind(actionId).first<{status: string; decided_by: string | null}>();
const recorded = async (h: Harness, actionId: string) => (await h.db.prepare('SELECT decision,grant_id FROM owner_grant_decisions WHERE action_id=? ORDER BY decision')
    .bind(actionId).all<{decision: string; grant_id: string}>()).results;
const execute = (h: Harness, as: AgentCredential, actionId: string) => h.ok(`/v1/external-actions/${actionId}/execute`, as, 'POST', {});

test('approveAction_OwnerLetsTheApp_ApprovedAsTheHumanGrantRecordedSentOnce', () => scenario(async ({h, calls}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    const actionId = await propose(h, agent);
    const approved = await tool(h, humanId, grantId, 'approve_action', {actionId, planDigest: await digestOf(h, humanId, actionId)});
    assert.deepEqual(approved, {status: 200, body: {actionId, status: 'approved'}});
    assert.deepEqual(await row(h, actionId), {status: 'approved', decided_by: humanId});
    assert.deepEqual(await recorded(h, actionId), [{decision: 'approved', grant_id: grantId}]);
    assert.equal(calls.length, 0, 'approving sends nothing');
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    assert.equal(calls.length, 1, 'executed once');
    const again = await tool(h, humanId, grantId, 'approve_action', {actionId, planDigest: await digestOf(h, humanId, actionId).catch(() => 'A'.repeat(43))});
    assert.equal(again.status, 409);
}));

test('approveAction_NotLetOrStopped_DecisionsOffNothingWritten', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const never = await grant(h, humanId, false), stopped = await grant(h, humanId);
    const actionId = await propose(h, agent);
    const planDigest = await digestOf(h, humanId, actionId);
    assert.deepEqual(await ownerAction(environment(h), {kind: 'stop-app-decisions', grantId: stopped, humanId, authAt: Date.now()} as OwnerAction), {ok: true});
    for (const grantId of [never, stopped]) {
        const approved = await tool(h, humanId, grantId, 'approve_action', {actionId, planDigest});
        assert.equal(approved.status, 403);
        assert.equal(codeOf(approved), 'decisions-off');
        const rejected = await tool(h, humanId, grantId, 'reject_action', {actionId});
        assert.equal(rejected.status, 403);
    }
    assert.deepEqual(await row(h, actionId), {status: 'pending', decided_by: null});
    assert.deepEqual(await recorded(h, actionId), []);
}));

test('approveAction_RevokedGrantOrFrozenHuman_401NothingWritten', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const revoked = await grant(h, humanId), live = await grant(h, humanId);
    const actionId = await propose(h, agent);
    const planDigest = await digestOf(h, humanId, actionId);
    await h.db.prepare('UPDATE owner_grants SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=?').bind(ticks(), revoked).run();
    assert.equal((await tool(h, humanId, revoked, 'approve_action', {actionId, planDigest})).status, 401);
    await h.db.prepare('UPDATE humans SET frozen_at=CAST(? AS INTEGER) WHERE human_id=?').bind(ticks(), humanId).run();
    assert.equal((await tool(h, humanId, live, 'approve_action', {actionId, planDigest})).status, 401);
    assert.equal((await tool(h, humanId, live, 'reject_action', {actionId})).status, 401);
    assert.deepEqual(await row(h, actionId), {status: 'pending', decided_by: null});
    assert.deepEqual(await recorded(h, actionId), []);
}));

test('approveAction_AnotherHumansAgent_NotFound', () => scenario(async ({h}) => {
    const mine = await ownedAgent(h), theirs = await ownedAgent(h);
    const grantId = await grant(h, mine.humanId);
    const actionId = await propose(h, theirs.agent);
    const planDigest = await digestOf(h, theirs.humanId, actionId);
    const approved = await tool(h, mine.humanId, grantId, 'approve_action', {actionId, planDigest});
    assert.equal(approved.status, 404);
    assert.equal(codeOf(approved), 'action-not-found');
    assert.equal((await tool(h, mine.humanId, grantId, 'reject_action', {actionId})).status, 404);
    // The grant's human comes from the verified grant: naming the other human does not reach their agent.
    assert.equal((await tool(h, theirs.humanId, grantId, 'approve_action', {actionId, planDigest})).status, 401);
    assert.deepEqual(await row(h, actionId), {status: 'pending', decided_by: null});
}));

test('approveAction_WrongDigestOrAccountChanged_RefusedAsOnTheOwnerPage', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    const actionId = await propose(h, agent);
    const planDigest = await digestOf(h, humanId, actionId);
    const wrong = await tool(h, humanId, grantId, 'approve_action', {actionId, planDigest: (planDigest[0] === 'A' ? 'B' : 'A') + planDigest.slice(1)});
    assert.equal(wrong.status, 409);
    assert.equal(codeOf(wrong), 'plan-changed');
    await setCredential(h, agent.accountId, 'rev-2');
    const changed = await tool(h, humanId, grantId, 'approve_action', {actionId, planDigest});
    assert.equal(changed.status, 409);
    assert.equal(codeOf(changed), 'account-changed');
    assert.deepEqual(await row(h, actionId), {status: 'pending', decided_by: null});
    assert.deepEqual(await recorded(h, actionId), []);
}));

test('approveAction_Expired_RefusedRejectStillAllowed', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    const actionId = await propose(h, agent);
    const planDigest = await digestOf(h, humanId, actionId);
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind(ticks(), actionId).run();
    const late = await tool(h, humanId, grantId, 'approve_action', {actionId, planDigest});
    assert.equal(late.status, 409);
    assert.equal(codeOf(late), 'expired');
    assert.deepEqual(await tool(h, humanId, grantId, 'reject_action', {actionId}), {status: 200, body: {actionId, status: 'rejected'}});
    assert.deepEqual(await recorded(h, actionId), [{decision: 'rejected', grant_id: grantId}]);
}));

test('rejectAction_WithdrawsAnUnusedApprovalButNeverOneBeingSent', () => scenario(async ({h, calls}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    const withdrawn = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId: withdrawn, planDigest: await digestOf(h, humanId, withdrawn)})).status, 200);
    assert.deepEqual(await tool(h, humanId, grantId, 'reject_action', {actionId: withdrawn}), {status: 200, body: {actionId: withdrawn, status: 'rejected'}});
    assert.deepEqual(await recorded(h, withdrawn), [{decision: 'approved', grant_id: grantId}, {decision: 'rejected', grant_id: grantId}]);
    assert.equal((await execute(h, agent, withdrawn)).status, 'rejected');

    const sent = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId: sent, planDigest: await digestOf(h, humanId, sent)})).status, 200);
    assert.equal((await execute(h, agent, sent)).status, 'completed');
    const late = await tool(h, humanId, grantId, 'reject_action', {actionId: sent});
    assert.equal(late.status, 409);
    assert.equal(codeOf(late), 'not-pending');
    assert.equal(calls.length, 1);
    for (const status of ['dispatching', 'unknown']) {
        await h.db.prepare('UPDATE external_actions SET status=? WHERE action_id=?').bind(status, sent).run();
        assert.equal((await tool(h, humanId, grantId, 'reject_action', {actionId: sent})).status, 409, status + ' is never reopened');
    }
}));

test('ownerPageDecision_WritesNoGrantRow', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    await grant(h, humanId);
    const actionId = await propose(h, agent);
    assert.deepEqual(await ownerAction(environment(h), {kind: 'confirm-action', requestId: actionId, planDigest: await digestOf(h, humanId, actionId),
        humanId, authAt: Date.now()} as OwnerAction), {ok: true});
    assert.deepEqual(await recorded(h, actionId), []);
}));

test('appDecisionTools_BadArgumentsOrReadOnly_RefusedNothingWritten', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    const actionId = await propose(h, agent);
    const planDigest = await digestOf(h, humanId, actionId);
    for (const [name, args] of [['approve_action', {actionId}], ['approve_action', {actionId, planDigest: 'short'}], ['approve_action', {actionId: actionId.toUpperCase(), planDigest}],
        ['reject_action', {actionId, planDigest}], ['reject_action', {}]] as const)
        assert.equal((await tool(h, humanId, grantId, name, args)).status, 400, name + ' ' + JSON.stringify(args));
    assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId, planDigest}, environment(h, {MAINTENANCE_MODE: 'true'}))).status, 503);
    assert.deepEqual(await row(h, actionId), {status: 'pending', decided_by: null});
    assert.deepEqual(await recorded(h, actionId), []);
}));

test('getPendingAction_TheOwnerPagesPlanForThisHumansPendingActionsOnly', () => scenario(async ({h}) => {
    const mine = await ownedAgent(h), theirs = await ownedAgent(h);
    const grantId = await grant(h, mine.humanId, false);
    const actionId = await propose(h, mine.agent), other = await propose(h, theirs.agent);
    const read = await tool(h, mine.humanId, grantId, 'get_pending_action', {actionId});
    assert.equal(read.status, 200);
    assert.equal(read.body.planDigest, await digestOf(h, mine.humanId, actionId), 'the digest the owner page confirms');
    assert.deepEqual([read.body.agentId, read.body.content, read.body.targetDisplay, read.body.accountChanged], [mine.agent.accountId, 'Episode 12 is live.', 'Desk Alpha', false]);
    assert.equal((await tool(h, mine.humanId, grantId, 'get_pending_action', {actionId: other})).status, 404, "another human's plan");
    const expiring = await propose(h, mine.agent);
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind(ticks(), expiring).run();
    assert.equal((await tool(h, mine.humanId, grantId, 'get_pending_action', {actionId: expiring})).status, 404, 'once expired');
    await ownerAction(environment(h), {kind: 'reject-action', requestId: actionId, humanId: mine.humanId, authAt: Date.now()} as OwnerAction);
    assert.equal((await tool(h, mine.humanId, grantId, 'get_pending_action', {actionId})).status, 404, 'once decided');
    await h.db.prepare('UPDATE owner_grants SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=?').bind(ticks(), grantId).run();
    assert.equal((await tool(h, mine.humanId, grantId, 'get_pending_action', {actionId: other})).status, 401);
}));

const decidedBy = async (h: Harness, humanId: string, actionId: string, clock?: () => bigint) => {
    const view = await ownerPendingAction(environment(h), humanId, actionId, clock);
    assert.ok(!view.frozen && view.settled, 'the owner page describes the settled action');
    return view.settled.decidedByApp;
};

test('ownerPendingAction_AppDecided_NamesTheGrantThatMadeTheDecisionTheStatusRestsOn', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    await h.db.prepare("UPDATE owner_grants SET client_name='Office Desk' WHERE grant_id=?").bind(grantId).run();
    const app = {grantId, clientId: 'https://office.example/client', clientName: 'Office Desk'};

    const approved = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId: approved, planDigest: await digestOf(h, humanId, approved)})).status, 200);
    assert.deepEqual(await decidedBy(h, humanId, approved), app, 'approved by the app');
    assert.equal((await execute(h, agent, approved)).status, 'completed');
    assert.deepEqual(await decidedBy(h, humanId, approved), app, 'sent after the app approved');

    const rejected = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'reject_action', {actionId: rejected})).status, 200);
    assert.deepEqual(await decidedBy(h, humanId, rejected), app, 'rejected by the app');

    // The owner withdraws the app's approval on Weave's own page: the rejection the status rests on is the owner's.
    const withdrawn = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId: withdrawn, planDigest: await digestOf(h, humanId, withdrawn)})).status, 200);
    assert.deepEqual(await ownerAction(environment(h), {kind: 'reject-action', requestId: withdrawn, humanId, authAt: Date.now()} as OwnerAction), {ok: true});
    assert.equal(await decidedBy(h, humanId, withdrawn), null, 'withdrawn on the owner page');

    // The app withdraws the owner's approval: the rejection is the app's.
    const appWithdrew = await propose(h, agent);
    assert.deepEqual(await ownerAction(environment(h), {kind: 'confirm-action', requestId: appWithdrew, planDigest: await digestOf(h, humanId, appWithdrew),
        humanId, authAt: Date.now()} as OwnerAction), {ok: true});
    assert.equal(await decidedBy(h, humanId, appWithdrew), null, 'approved on the owner page');
    assert.equal((await tool(h, humanId, grantId, 'reject_action', {actionId: appWithdrew})).status, 200);
    assert.deepEqual(await decidedBy(h, humanId, appWithdrew), app, 'then withdrawn by the app');
}));

test('ownerPendingAction_OwnerDecidedOrExpiredUndecided_NoApp_AppApprovedThenExpired_NamesApp', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    const ownerRejected = await propose(h, agent);
    assert.deepEqual(await ownerAction(environment(h), {kind: 'reject-action', requestId: ownerRejected, humanId, authAt: Date.now()} as OwnerAction), {ok: true});
    assert.equal(await decidedBy(h, humanId, ownerRejected), null);
    const later = () => BigInt(ticks()) + 2n * 24n * 60n * 60n * 10_000_000n;
    const expired = await propose(h, agent);
    assert.equal(await decidedBy(h, humanId, expired, later), null, 'expired undecided');
    const approvedThenExpired = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId: approvedThenExpired, planDigest: await digestOf(h, humanId, approvedThenExpired)})).status, 200);
    const view = await ownerPendingAction(environment(h), humanId, approvedThenExpired, later);
    assert.ok(!view.frozen && view.settled?.status === 'expired');
    assert.equal(view.settled.decidedByApp?.grantId, grantId, 'an app approval that expired unsent still names the app');
}));

const recentApps = async (h: Harness, humanId: string) => {
    const overview = await ownerOverview(environment(h), humanId) as {recentActions: {actionId: string; status: string; decidedByApp: unknown}[]};
    return new Map(overview.recentActions.map(a => [a.actionId, a.decidedByApp]));
};

test('ownerOverview_RecentActions_NameTheAppThatDecided_OwnerDecisionsAndOtherHumansNone', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    await h.db.prepare("UPDATE owner_grants SET client_name='Office Desk' WHERE grant_id=?").bind(grantId).run();
    const app = {grantId, clientId: 'https://office.example/client', clientName: 'Office Desk'};

    const appApprovedSent = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId: appApprovedSent, planDigest: await digestOf(h, humanId, appApprovedSent)})).status, 200);
    assert.equal((await execute(h, agent, appApprovedSent)).status, 'completed');
    const appRejected = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'reject_action', {actionId: appRejected})).status, 200);
    // The app approved, the owner withdrew it on Weave's own page: the rejection the status rests on is the owner's.
    const ownerWithdrew = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId: ownerWithdrew, planDigest: await digestOf(h, humanId, ownerWithdrew)})).status, 200);
    assert.deepEqual(await ownerAction(environment(h), {kind: 'reject-action', requestId: ownerWithdrew, humanId, authAt: Date.now()} as OwnerAction), {ok: true});
    // The owner approved on Weave's page, the app withdrew it: the app's rejection.
    const appWithdrew = await propose(h, agent);
    assert.deepEqual(await ownerAction(environment(h), {kind: 'confirm-action', requestId: appWithdrew, planDigest: await digestOf(h, humanId, appWithdrew),
        humanId, authAt: Date.now()} as OwnerAction), {ok: true});
    const ownerApproved = await propose(h, agent);
    assert.deepEqual(await ownerAction(environment(h), {kind: 'confirm-action', requestId: ownerApproved, planDigest: await digestOf(h, humanId, ownerApproved),
        humanId, authAt: Date.now()} as OwnerAction), {ok: true});
    assert.equal((await tool(h, humanId, grantId, 'reject_action', {actionId: appWithdrew})).status, 200);

    const listed = await recentApps(h, humanId);
    assert.deepEqual(listed.get(appApprovedSent), app, 'approved by the app and sent');
    assert.deepEqual(listed.get(appRejected), app, 'rejected by the app');
    assert.equal(listed.get(ownerWithdrew), null, 'withdrawn on the owner page');
    assert.deepEqual(listed.get(appWithdrew), app, 'withdrawn by the app');
    assert.equal(listed.get(ownerApproved), null, 'approved on the owner page');
    assert.equal(listed.size, 5, 'each action listed once');

    // Another human's app decisions never show on this human's list, and that human sees their own.
    const other = await ownedAgent(h);
    const otherGrant = await grant(h, other.humanId);
    const theirs = await propose(h, other.agent);
    assert.equal((await tool(h, other.humanId, otherGrant, 'reject_action', {actionId: theirs})).status, 200);
    assert.equal((await recentApps(h, humanId)).has(theirs), false);
    assert.equal(((await recentApps(h, other.humanId)).get(theirs) as {grantId: string}).grantId, otherGrant);
}));

test('ownerOverview_RecentActions_AppApprovedThenExpiredUnsent_NamesApp_ExpiredUndecidedNone', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    const approved = await propose(h, agent), undecided = await propose(h, agent);
    assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId: approved, planDigest: await digestOf(h, humanId, approved)})).status, 200);
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id IN (?,?)').bind(ticks(), approved, undecided).run();
    const overview = await ownerOverview(environment(h), humanId) as {recentActions: {actionId: string; status: string; decidedByApp: {grantId: string} | null}[]};
    const byId = new Map(overview.recentActions.map(a => [a.actionId, a]));
    assert.equal(byId.get(approved)?.status, 'expired');
    assert.equal(byId.get(approved)?.decidedByApp?.grantId, grantId, 'an app approval that expired unsent');
    assert.equal(byId.get(undecided)?.status, 'expired');
    assert.equal(byId.get(undecided)?.decidedByApp, null, 'expired undecided');
}));

test('ownerOverview_RecentActions_AppApprovedThenRejected_PagesEachOnceNamingTheApp', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    const ids: string[] = [];
    for (let i = 0; i < 25; i++) {
        const actionId = await propose(h, agent);
        // Half carry both decision rows (the app approved, then withdrew it): the list's join must still yield one entry each.
        if (i % 2 === 0) assert.equal((await tool(h, humanId, grantId, 'approve_action', {actionId, planDigest: await digestOf(h, humanId, actionId)})).status, 200);
        assert.equal((await tool(h, humanId, grantId, 'reject_action', {actionId})).status, 200);
        ids.push(actionId);
    }
    const page = async (before: string | null) => {
        const overview = await ownerOverview(environment(h), humanId, null, undefined, undefined, before);
        if (overview.frozen) throw Error('frozen');
        return overview;
    };
    const first = await page(null);
    assert.equal(first.recentActions.length, 20);
    assert.equal(first.moreRecentActions, true);
    const second = await page(first.recentActions.at(-1)!.actionId);
    assert.equal(second.recentActions.length, 5);
    assert.equal(second.moreRecentActions, false);
    const listed = [...first.recentActions, ...second.recentActions];
    assert.deepEqual(new Set(listed.map(a => a.actionId)), new Set(ids), 'each listed once across the pages');
    assert.ok(listed.every(a => a.status === 'rejected' && a.decidedByApp?.grantId === grantId), 'each names the app');
}));
