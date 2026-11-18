import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { ownerAction, ownerOverview, reauthWindowMs, type OwnerAction } from '../src/owner-actions.ts';
import { actionLifetimeTicks, approvalOrigin, recentActionWindowTicks, recentActionsShownToOwner, executeExternalAction, listWaitingExternalActions, ownerPendingAction, planDigest, proposalKey, proposeExternalAction, type ActionCaller } from '../src/external-actions.ts';
import { apiTime } from '../src/time.ts';
import { createOwnedAgent } from '../src/humans.ts';
import type { Problem } from '../src/wire.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Call = {operation: string; content: string; targetAccount: string; actionId: string};
type Provider = {calls: Call[]; respond: (call: Call) => Response | Promise<Response>};

const endpoint = 'https://provider.test/post';
const completed = (call: Call) => Response.json({externalId: 'post-' + call.actionId});

async function scenario(run: (s: {h: Harness; provider: Provider}) => Promise<void>, configured = true) {
    const provider: Provider = {calls: [], respond: completed};
    const h = await start({bindings: configured ? {EXTERNAL_ACTION_TEST_ENDPOINT: endpoint} : {}, outboundService: async (request: Request) => {
        if (request.url !== endpoint) return new Response('unexpected', {status: 599});
        const call = await request.json() as Call;
        provider.calls.push(call);
        return provider.respond(call);
    }});
    try { await run({h, provider}); } finally { await h.close(); }
}

const environment = (h: Harness, overrides: Partial<Env> = {}) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100',
    REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100', EXTERNAL_ACTION_TEST_ENDPOINT: endpoint, ...overrides}) as unknown as Env;
const ticks = () => (BigInt(Date.now()) * 10000n).toString();

async function setCredential(h: Harness, accountId: string, revision = 'rev-1', target = 'urn:li:person:alpha', display = 'Desk Alpha') {
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
        VALUES(?,'linkedin-main','test-http',?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))
        ON CONFLICT(account_id,credential_ref) DO UPDATE SET revision=excluded.revision,target_account=excluded.target_account,
        target_display=excluded.target_display,revoked_at=NULL,updated_at=excluded.updated_at`).bind(accountId, revision, target, display, ticks(), ticks()).run();
}

async function ownedAgent(h: Harness) {
    const agent = await h.register();
    const humanId = await h.claim(agent);
    await setCredential(h, agent.accountId);
    return {agent, humanId};
}

const propose = (h: Harness, as: AgentCredential, content = 'Episode 12 is live.') =>
    h.request('/v1/external-actions', as, 'POST', {credentialRef: 'linkedin-main', operation: 'publish_text', content});
const execute = (h: Harness, as: AgentCredential, actionId: string) => h.ok(`/v1/external-actions/${actionId}/execute`, as, 'POST', {});
const decide = (h: Harness, humanId: string, action: Record<string, unknown>) =>
    ownerAction(environment(h), {humanId, authAt: Date.now(), ...action} as OwnerAction);

async function pendingFor(h: Harness, humanId: string) {
    const overview = await ownerOverview(environment(h), humanId);
    assert.equal(overview.frozen, false);
    return (overview as Extract<typeof overview, {frozen: false}>).externalActions;
}

async function approved(h: Harness) {
    const {agent, humanId} = await ownedAgent(h);
    const proposed = await propose(h, agent);
    assert.equal(proposed.status, 200, await proposed.clone().text());
    const action = await proposed.json() as {actionId: string; status: string};
    const [shown] = await pendingFor(h, humanId);
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: shown.planDigest}), {ok: true});
    return {agent, humanId, actionId: action.actionId};
}

test('executeExternalAction_ApprovedPlan_SendsExactlyOnceAfterApproval', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string; status: string};
    assert.equal(action.status, 'pending');
    assert.equal((await execute(h, agent, action.actionId)).status, 'pending');

    const [shown] = await pendingFor(h, humanId);
    assert.equal(shown.content, 'Episode 12 is live.');
    assert.equal(shown.targetDisplay, 'Desk Alpha');
    assert.equal(shown.targetAccount, 'urn:li:person:alpha');
    assert.equal(shown.credentialRef + '@' + shown.credentialRevision, 'linkedin-main@rev-1');
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: shown.planDigest}), {ok: true});
    assert.equal(provider.calls.length, 0);

    const first = await execute(h, agent, action.actionId);
    const again = await execute(h, agent, action.actionId);

    assert.equal(first.status, 'completed');
    assert.equal(first.receipt, 'post-' + action.actionId);
    assert.deepEqual(again, first);
    assert.deepEqual(provider.calls, [{operation: 'publish_text', content: 'Episode 12 is live.', targetAccount: 'urn:li:person:alpha', actionId: action.actionId}]);
    assert.deepEqual(await pendingFor(h, humanId), []);
}));

test('proposeExternalAction_NoFirstPartyAuthorizationServer_NoApprovalUrl', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {status: string; approvalUrl: string | null};
    assert.equal(action.status, 'pending');
    assert.equal(action.approvalUrl, null, 'without a first-party authorization server there is no page to link to');
}));

test('approvalOrigin_OnlyAFirstPartyHttpsIssuer_HasThePage', () => {
    const service = {} as Env['AUTH_SERVER'];
    assert.equal(approvalOrigin({AUTH_SERVER: service, OAUTH_ISSUER: 'https://auth.weave.example'}), 'https://auth.weave.example');
    assert.equal(approvalOrigin({AUTH_SERVER: service, OAUTH_ISSUER: 'https://auth.weave.example/'}), 'https://auth.weave.example');
    // A third-party issuer reached by introspection hosts no Weave owner page.
    assert.equal(approvalOrigin({OAUTH_ISSUER: 'https://idp.example'}), null);
    for (const issuer of [undefined, '', 'http://auth.weave.example', 'not a url'])
        assert.equal(approvalOrigin({AUTH_SERVER: service, OAUTH_ISSUER: issuer}), null, String(issuer));
});

test('proposeExternalAction_UnownedAgent_Conflict', () => scenario(async ({h}) => {
    const agent = await h.register();
    await setCredential(h, agent.accountId);
    const response = await propose(h, agent);
    assert.equal(response.status, 409);
    assert.equal((await response.json() as {reason: string}).reason, 'unowned-agent');
}));

test('proposeExternalAction_NoProviderConfigured_Conflict', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const response = await propose(h, agent);
    assert.equal(response.status, 409);
    assert.equal((await response.json() as {reason: string}).reason, 'provider-unavailable');
}, false));

// A deploy that no longer runs the adapter an approved plan names (the propose side answers 409 provider-unavailable):
// the plan is refused at dispatch and never sent, even with a matching digest and credential. Only the dispatch's
// provider lookup can catch this.
test('executeExternalAction_AdapterRemovedSinceApproval_FailsPlanChangedWithoutSending', () => scenario(async ({h, provider}) => {
    const {agent, actionId} = await approved(h);
    const row = await h.db.prepare(`SELECT action_id,account_id,provider,contract_revision,operation,content,credential_ref,credential_revision,
        target_account,CAST(expires_at AS TEXT) expires_at FROM external_actions WHERE action_id=?`).bind(actionId).first<Parameters<typeof planDigest>[0]>();
    const retired = {...row!, provider: 'retired-platform'};
    await h.db.batch([
        h.db.prepare(`UPDATE external_actions SET provider=?,plan_digest=? WHERE action_id=?`).bind(retired.provider, await planDigest(retired), actionId),
        h.db.prepare(`UPDATE external_credentials SET provider=? WHERE account_id=? AND credential_ref='linkedin-main'`).bind(retired.provider, agent.accountId),
    ]);

    const result = await execute(h, agent, actionId);
    const again = await execute(h, agent, actionId);

    assert.equal(result.status, 'failed');
    assert.equal(result.failure, 'plan-changed');
    assert.equal(result.dispatchedAt, null);
    assert.deepEqual(again, result);
    assert.equal(provider.calls.length, 0);
}));

test('proposeExternalAction_InvalidInput_Rejected', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    for (const input of [{credentialRef: 'missing', operation: 'publish_text', content: 'x'}, {credentialRef: 'linkedin-main', operation: 'delete_everything', content: 'x'},
        {credentialRef: 'linkedin-main', operation: 'publish_text', content: '  '}, {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'x'.repeat(8001)},
        {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'x', account: 'urn:li:person:other'}])
        assert.equal((await h.request('/v1/external-actions', agent, 'POST', input)).status, 400, JSON.stringify(input).slice(0, 80));
}));

// Apps show the refused field and reason; a malformed reference must not read as an unknown one, and each limit admits its edge.
test('proposeExternalAction_MalformedField_400NamesFieldAndReasonAndStoresNothing', () => scenario(async ({h, provider}) => {
    const {agent} = await ownedAgent(h);
    const refRule = 'must be 1 to 64 characters of letters, digits, ".", "_", ":" or "-"';
    const contentRule = 'must be non-blank text of at most 8000 characters';
    const base = {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'x'};
    const cases: [Record<string, unknown>, string | undefined, string | undefined][] = [
        [{...base, credentialRef: 'a'.repeat(65)}, 'credentialRef', refRule],
        [{...base, credentialRef: ''}, 'credentialRef', refRule],
        [{...base, credentialRef: 'linkedin main'}, 'credentialRef', refRule],
        [{...base, credentialRef: 'linkedin/main'}, 'credentialRef', refRule],
        [{...base, credentialRef: 7}, 'credentialRef', refRule],
        [{...base, credentialRef: 'missing'}, 'credentialRef', 'must name a live credential of this agent'],
        [{...base, operation: 7}, 'operation', 'must be a supported operation'],
        [{...base, operation: 'delete_everything'}, 'operation', 'must be a supported operation'],
        [{...base, content: 7}, 'content', contentRule],
        [{...base, content: ' \n\t '}, 'content', contentRule],
        [{...base, content: 'x'.repeat(8001)}, 'content', contentRule],
        // The body's shape is checked first and names no field.
        [{credentialRef: 'linkedin-main', operation: 'publish_text'}, undefined, undefined],
        [{...base, extra: true}, undefined, undefined],
    ];
    for (const [input, field, reason] of cases) {
        const why = JSON.stringify(input).slice(0, 80);
        const response = await h.request('/v1/external-actions', agent, 'POST', input);
        assert.equal(response.status, 400, why);
        const problem = await response.json() as {code: string; field?: string; reason?: string};
        assert.deepEqual([problem.code, problem.field, problem.reason], ['invalid', field, reason], why);
    }
    const stored = await h.db.prepare('SELECT count(*) n FROM external_actions WHERE account_id=?').bind(agent.accountId).first<number>('n');
    assert.equal(stored, 0, 'a refused proposal stores no plan');

    // Positive controls at each edge: a 64-character reference and 8000 characters of content are admitted.
    const longRef = 'L'.repeat(64);
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
        VALUES(?,?,'test-http','rev-1','urn:li:person:alpha','Desk Alpha',CAST(? AS INTEGER),CAST(? AS INTEGER))`).bind(agent.accountId, longRef, ticks(), ticks()).run();
    for (const input of [{...base, credentialRef: longRef}, {...base, content: 'x'.repeat(8000)}]) {
        const response = await h.request('/v1/external-actions', agent, 'POST', input);
        assert.equal(response.status, 200, await response.clone().text());
        assert.equal((await response.json() as {status: string}).status, 'pending');
    }
    assert.equal(provider.calls.length, 0);
}));

// Another digest is a stale page (approve again after reloading); a reconnected account can never be approved again.
test('confirmExternalAction_DigestOrCredentialChanged_PlanChangedOrAccountChanged', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const [shown] = await pendingFor(h, humanId);
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: 'A'.repeat(43)}), {ok: false, reason: 'plan-changed'});
    await setCredential(h, agent.accountId, 'rev-2');
    assert.equal((await pendingFor(h, humanId))[0].accountChanged, true, 'a reconnected account is flagged on the owner page');
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: shown.planDigest}), {ok: false, reason: 'account-changed'});
    assert.equal((await execute(h, agent, action.actionId)).status, 'pending');
    assert.equal(provider.calls.length, 0);
}));

// A malformed digest is not a different plan: the owner page never sent it, so it reads as not-found before the plan is compared.
test('confirmExternalAction_MalformedPlanDigest_NotFoundAndStillPending', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const [shown] = await pendingFor(h, humanId);
    for (const planDigest of [undefined, 43, shown.planDigest.slice(1), shown.planDigest + 'A', shown.planDigest.slice(1) + '=', shown.planDigest.slice(1) + '+'])
        assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest}), {ok: false, reason: 'not-found'}, String(planDigest));
    assert.equal((await execute(h, agent, action.actionId)).status, 'pending');
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: shown.planDigest}), {ok: true});
    assert.equal(provider.calls.length, 0);
}));

test('confirmExternalAction_StaleAuthentication_Refused', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const [shown] = await pendingFor(h, humanId);
    const stale = await ownerAction(environment(h), {humanId, authAt: Date.now() - reauthWindowMs - 1000, kind: 'confirm-action',
        requestId: action.actionId, planDigest: shown.planDigest} as OwnerAction);
    assert.deepEqual(stale, {ok: false, reason: 'stale-authentication'});
}));

// Rejecting is protective, like rejecting a binding or key request: it needs no fresh sign-in, so an owner can always stop a plan.
test('rejectExternalAction_DayOldAuthentication_RejectedAndNeverSent', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const old = await ownerAction(environment(h), {humanId, authAt: Date.now() - 86_400_000, kind: 'reject-action', requestId: action.actionId} as OwnerAction);
    assert.deepEqual(old, {ok: true});
    assert.equal((await execute(h, agent, action.actionId)).status, 'rejected');
    assert.equal(provider.calls.length, 0);
}));

test('confirmExternalAction_AnotherHumansAgent_NotFound', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const other = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    assert.deepEqual(await decide(h, other.humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: 'A'.repeat(43)}), {ok: false, reason: 'not-found'});
    assert.deepEqual(await decide(h, other.humanId, {kind: 'reject-action', requestId: action.actionId}), {ok: false, reason: 'not-found'});
}));

// The approval-time credential re-check: the owner reviewed the plan for one connected account, which no longer stands.
// It matches execute's credential check, so the owner is never asked to approve a plan that execute would refuse unsent.
for (const change of ['revoked', 'retargeted', 'reprovidered'] as const)
    test(`confirmExternalAction_CredentialChangedWhilePending_${change}_AccountChangedAndStillPending`, () => scenario(async ({h, provider}) => {
        const {agent, humanId} = await ownedAgent(h);
        const action = await (await propose(h, agent)).json() as {actionId: string};
        const [shown] = await pendingFor(h, humanId);
        assert.equal(shown.accountChanged, false, 'a plan whose credential still matches can be approved');
        if (change === 'retargeted') await setCredential(h, agent.accountId, 'rev-1', 'urn:li:person:beta', 'Desk Beta');
        // Same reference, revision and target, but another platform.
        else if (change === 'reprovidered') await h.db.prepare(`UPDATE external_credentials SET provider='telegram' WHERE account_id=?`).bind(agent.accountId).run();
        else await h.db.prepare('UPDATE external_credentials SET revoked_at=CAST(? AS INTEGER) WHERE account_id=?').bind(ticks(), agent.accountId).run();

        // The owner page learns it before the owner tries: the plan is still listed, flagged so only rejecting is offered.
        const [listed] = await pendingFor(h, humanId);
        assert.equal(listed.actionId, action.actionId);
        assert.equal(listed.accountChanged, true);
        assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: shown.planDigest}), {ok: false, reason: 'account-changed'});
        assert.equal((await execute(h, agent, action.actionId)).status, 'pending');
        assert.equal(provider.calls.length, 0);
    }));

// The approval-time plan re-check: the same adapter presence, contract revision and recomputed digest that execute's claim()
// checks. Each case leaves the digest the page showed equal to the stored one, so only the re-check can refuse it.
const pendingPlanChanges = {
    // A stored field edited without its digest: the recomputed digest no longer matches.
    contentEdited: async (h: Harness, _agent: AgentCredential, actionId: string) =>
        h.db.prepare(`UPDATE external_actions SET content='Episode 13 is live.' WHERE action_id=?`).bind(actionId).run(),
    // A deploy changed what the adapter sends; the stored digest still matches the stored (older) contract.
    contractChanged: async (h: Harness, _agent: AgentCredential, actionId: string) => restamp(h, actionId, {contract_revision: 'test-http-v0'}),
    // A deploy no longer runs the adapter the plan names; the credential follows so only the adapter lookup differs.
    adapterRemoved: async (h: Harness, agent: AgentCredential, actionId: string) => {
        await restamp(h, actionId, {provider: 'retired-platform'});
        await h.db.prepare(`UPDATE external_credentials SET provider='retired-platform' WHERE account_id=? AND credential_ref='linkedin-main'`).bind(agent.accountId).run();
    },
} as const;

/** Rewrites stored plan fields together with a digest that matches them, as a plan proposed under those fields would carry. */
async function restamp(h: Harness, actionId: string, fields: Partial<Parameters<typeof planDigest>[0]>) {
    const row = await h.db.prepare(`SELECT action_id,account_id,provider,contract_revision,operation,content,credential_ref,credential_revision,
        target_account,CAST(expires_at AS TEXT) expires_at FROM external_actions WHERE action_id=?`).bind(actionId).first<Parameters<typeof planDigest>[0]>();
    const changed = {...row!, ...fields};
    await h.db.prepare(`UPDATE external_actions SET provider=?,contract_revision=?,plan_digest=? WHERE action_id=?`)
        .bind(changed.provider, changed.contract_revision, await planDigest(changed), actionId).run();
}

for (const [change, apply] of Object.entries(pendingPlanChanges))
    test(`confirmExternalAction_PlanNoLongerDispatchable_${change}_PlanChangedAndStillPending`, () => scenario(async ({h, provider}) => {
        const {agent, humanId} = await ownedAgent(h);
        const action = await (await propose(h, agent)).json() as {actionId: string};
        await apply(h, agent, action.actionId);
        const stored = await h.db.prepare('SELECT plan_digest FROM external_actions WHERE action_id=?').bind(action.actionId).first<{plan_digest: string}>();

        assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: stored!.plan_digest}), {ok: false, reason: 'plan-changed'});
        assert.equal((await execute(h, agent, action.actionId)).status, 'pending');
        assert.equal(provider.calls.length, 0);
    }));

// Positive control for the re-check: an unchanged plan proposed through a configured adapter is approved.
test('confirmExternalAction_UnchangedDispatchablePlan_Approved', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const [shown] = await pendingFor(h, humanId);
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: shown.planDigest}), {ok: true});
}));

test('confirmExternalAction_PastExpiry_ExpiredWhileRejectStillDecides', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const [shown] = await pendingFor(h, humanId);
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind((BigInt(ticks()) - 1n).toString(), action.actionId).run();

    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: shown.planDigest}), {ok: false, reason: 'expired'});
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: action.actionId}), {ok: true});
    assert.equal((await execute(h, agent, action.actionId)).status, 'rejected');
    assert.equal(provider.calls.length, 0);
}));

// The owner page lists every live pending plan of the human's agents (each agent holds at most 20), never an expired one or another human's.
test('ownerOverview_ExternalActions_EveryLivePendingPlanOfOwnedAgentsOnly', () => scenario(async ({h}) => {
    const {agent: busy, humanId} = await ownedAgent(h);
    const quiet = await h.register();
    await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(humanId, quiet.accountId).run();
    await setCredential(h, quiet.accountId);
    const {agent: stranger} = await ownedAgent(h);
    const proposeOk = async (as: AgentCredential, content: string) => {
        const response = await propose(h, as, content);
        assert.equal(response.status, 200, await response.clone().text());
        return (await response.json() as {actionId: string}).actionId;
    };
    const busyIds: string[] = [];
    for (let i = 0; i < 20; i++) busyIds.push(await proposeOk(busy, `Episode ${i} is live.`));
    const quietId = await proposeOk(quiet, 'Quiet episode is live.');
    const strangerId = await proposeOk(stranger, 'Not yours.');

    const listed = await pendingFor(h, humanId);
    assert.equal(listed.length, 21, 'a full agent does not crowd out another agent of the same owner');
    assert.deepEqual(new Set(listed.map(action => action.actionId)), new Set([...busyIds, quietId]));
    assert.ok(!listed.some(action => action.actionId === strangerId), "another human's plan is never listed");

    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind((BigInt(ticks()) - 1n).toString(), quietId).run();
    const afterExpiry = await pendingFor(h, humanId);
    assert.equal(afterExpiry.length, 20);
    assert.ok(!afterExpiry.some(action => action.actionId === quietId), 'an expired plan is not offered for approval');
}));

test('decideExternalAction_AlreadyDecided_NotPendingAndOutcomeKept',() => scenario(async ({h, provider}) => {
    const {agent, humanId, actionId} = await approved(h);
    const second = await (await propose(h, agent, 'Episode 13 is live.')).json() as {actionId: string};
    const [shown] = await pendingFor(h, humanId);
    assert.equal(shown.actionId, second.actionId);
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: second.actionId}), {ok: true});

    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: actionId}), {ok: false, reason: 'not-pending'});
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: second.actionId, planDigest: shown.planDigest}), {ok: false, reason: 'not-pending'});
    assert.equal((await execute(h, agent, second.actionId)).status, 'rejected');
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    assert.equal(provider.calls.length, 1);
}));

for (const change of ['rotated', 'revoked', 'retargeted', 'reprovidered'] as const)
    test(`executeExternalAction_CredentialChangedAfterApproval_${change}_FailsWithoutSending`, () => scenario(async ({h, provider}) => {
        const {agent, actionId} = await approved(h);
        if (change === 'rotated') await setCredential(h, agent.accountId, 'rev-2');
        else if (change === 'retargeted') await setCredential(h, agent.accountId, 'rev-1', 'urn:li:person:beta', 'Desk Beta');
        // Same reference, revision and target, but another platform: the plan's provider still exists, so only the credential check refuses.
        else if (change === 'reprovidered') await h.db.prepare(`UPDATE external_credentials SET provider='telegram' WHERE account_id=?`).bind(agent.accountId).run();
        else await h.db.prepare('UPDATE external_credentials SET revoked_at=CAST(? AS INTEGER) WHERE account_id=?').bind(ticks(), agent.accountId).run();

        const result = await execute(h, agent, actionId);
        const again = await execute(h, agent, actionId);

        assert.equal(result.status, 'failed');
        assert.equal(result.failure, 'credential-changed');
        assert.equal(result.dispatchedAt, null);
        assert.equal(again.status, 'failed');
        assert.equal(provider.calls.length, 0);
    }));

test('executeExternalAction_OwnerFrozenAfterApproval_FailsWithoutSending', () => scenario(async ({h, provider}) => {
    const {agent, humanId, actionId} = await approved(h);
    await h.db.prepare('UPDATE humans SET frozen_at=CAST(? AS INTEGER) WHERE human_id=?').bind(ticks(), humanId).run();
    const result = await execute(h, agent, actionId);
    assert.equal(result.status, 'failed');
    assert.equal(result.failure, 'owner-frozen');
    assert.equal(provider.calls.length, 0);
}));

// No product path moves a bound agent today; the dispatch re-check is what keeps an approval from outliving its approver's ownership.
for (const change of ['anotherHuman', 'unowned'] as const)
    test(`executeExternalAction_OwnerChangedAfterApproval_${change}_FailsWithoutSending`, () => scenario(async ({h, provider}) => {
        const {agent, actionId} = await approved(h);
        const next = change === 'anotherHuman' ? (await ownedAgent(h)).humanId : null;
        await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(next, agent.accountId).run();

        const result = await execute(h, agent, actionId);
        const again = await execute(h, agent, actionId);

        assert.equal(result.status, 'failed');
        assert.equal(result.failure, 'owner-changed');
        assert.equal(result.dispatchedAt, null);
        assert.equal(again.status, 'failed');
        assert.equal(again.failure, 'owner-changed');
        assert.equal(provider.calls.length, 0);
    }));

test('executeExternalAction_ApprovalExpired_DoesNotSend',() => scenario(async ({h, provider}) => {
    const {agent, actionId} = await approved(h);
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind((BigInt(ticks()) - 1n).toString(), actionId).run();
    assert.equal((await execute(h, agent, actionId)).status, 'expired');
    assert.equal(provider.calls.length, 0);
}));

// Each stored field the digest binds, changed after the owner confirmed it. Only the dispatch re-check can catch these.
const planEdits = {
    content: `content='Episode 13 is live.'`,
    operation: `operation='publish_other'`,
    contractRevision: `contract_revision='test-http-v0'`,
    expiryExtended: `expires_at=expires_at+10000`,
} as const;

for (const [field, assignment] of Object.entries(planEdits))
    test(`executeExternalAction_PlanEditedAfterApproval_${field}_FailsPlanChangedWithoutSending`, () => scenario(async ({h, provider}) => {
        const {agent, actionId} = await approved(h);
        await h.db.prepare(`UPDATE external_actions SET ${assignment} WHERE action_id=?`).bind(actionId).run();

        const result = await execute(h, agent, actionId);
        const again = await execute(h, agent, actionId);

        assert.equal(result.status, 'failed');
        assert.equal(result.failure, 'plan-changed');
        assert.equal(result.dispatchedAt, null);
        assert.equal(again.status, 'failed');
        assert.equal(provider.calls.length, 0);
    }));

// A plan approved under an earlier contract of its adapter, with a digest that still matches its stored fields: a deploy
// that changed what the adapter sends must not send it. Only the dispatch's contract check can catch this.
test('executeExternalAction_AdapterContractChangedSinceApproval_FailsPlanChangedWithoutSending', () => scenario(async ({h, provider}) => {
    const {agent, actionId} = await approved(h);
    const row = await h.db.prepare(`SELECT action_id,account_id,provider,contract_revision,operation,content,credential_ref,credential_revision,
        target_account,CAST(expires_at AS TEXT) expires_at FROM external_actions WHERE action_id=?`).bind(actionId).first<Parameters<typeof planDigest>[0]>();
    assert.equal(row!.contract_revision, 'test-http-v1');
    const earlier = {...row!, contract_revision: 'test-http-v0'};
    await h.db.prepare(`UPDATE external_actions SET contract_revision=?,plan_digest=? WHERE action_id=?`)
        .bind(earlier.contract_revision, await planDigest(earlier), actionId).run();

    const result = await execute(h, agent, actionId);
    const again = await execute(h, agent, actionId);

    assert.equal(result.status, 'failed');
    assert.equal(result.failure, 'plan-changed');
    assert.equal(result.dispatchedAt, null);
    assert.equal(again.status, 'failed');
    assert.equal(provider.calls.length, 0);
}));

test('executeExternalAction_Rejected_NeverSends', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: action.actionId}), {ok: true});
    assert.equal((await execute(h, agent, action.actionId)).status, 'rejected');
    assert.equal(provider.calls.length, 0);
}));

for (const [name, respond] of [['ServerError', () => new Response('{}', {status: 502})], ['MalformedReceipt', () => Response.json({})],
    ['ResponseLost', () => { throw Error('connection reset'); }]] as const)
    test(`executeExternalAction_${name}_RecordsUnknownAndNeverResends`, () => scenario(async ({h, provider}) => {
        const {agent, actionId} = await approved(h);
        provider.respond = respond;
        const first = await execute(h, agent, actionId);
        provider.respond = completed;
        const again = await execute(h, agent, actionId);

        assert.equal(first.status, 'unknown');
        assert.equal(first.failure, 'outcome-unknown');
        assert.equal(again.status, 'unknown');
        assert.equal(provider.calls.length, 1);
    }));

test('executeExternalAction_ProviderConfirmedRejection_Failed', () => scenario(async ({h, provider}) => {
    const {agent, actionId} = await approved(h);
    provider.respond = () => Response.json({reason: 'duplicate content'}, {status: 422});
    const result = await execute(h, agent, actionId);
    assert.equal(result.status, 'failed');
    assert.equal(result.failure, 'provider-rejected: duplicate content');
    assert.notEqual(result.dispatchedAt, null);
    assert.equal(provider.calls.length, 1);
}));

test('executeExternalAction_RejectionReasonWithControlsAndSplitPair_StoredVisibleAndWellFormed', () => scenario(async ({h, provider}) => {
    const {agent, actionId} = await approved(h);
    provider.respond = () => Response.json({reason: 'Bad\u0000 \u202etoken\ud800 ' + 'x'.repeat(240) + '\u{1F600}'.repeat(10)}, {status: 422});
    const result = await execute(h, agent, actionId);
    assert.equal(result.status, 'failed');
    const reason = result.failure.slice('provider-rejected: '.length);
    assert.ok(result.failure.startsWith('provider-rejected: Bad token\uFFFD x'), result.failure);
    assert.ok(reason.isWellFormed(), 'no half surrogate pair is stored');
    assert.ok(reason.length <= 256 && reason.length >= 255, String(reason.length));
    assert.doesNotMatch(reason, /[\u0000-\u001f\u202e]/u);
}));

test('executeExternalAction_ConcurrentExecutes_SendOnce', () => scenario(async ({h, provider}) => {
    const {agent, actionId} = await approved(h);
    const results = await Promise.all(Array.from({length: 5}, () => execute(h, agent, actionId)));
    assert.equal(provider.calls.length, 1);
    assert.ok(results.every(result => ['dispatching', 'completed'].includes(result.status)), JSON.stringify(results.map(result => result.status)));
    assert.equal((await h.ok(`/v1/external-actions/${actionId}`, agent)).status, 'completed');
}));

// Envoylet #166 tells the owner that a plan still dispatching long after dispatchedAt will never be sent again, by Weave or by
// Envoylet. That holds only if a provider call whose outcome write failed leaves the row 'dispatching' and no later execute,
// expiry or cron sweep sends it again or reports it as finished. A trigger refuses the outcome write, so the real catch runs.
test('executeExternalAction_OutcomeWriteFails_StaysDispatchingAndNeverResends', () => scenario(async ({h, provider}) => {
    const {agent, actionId} = await approved(h);
    await h.db.prepare(`CREATE TRIGGER refuse_outcome BEFORE UPDATE OF status ON external_actions WHEN OLD.status='dispatching'
        BEGIN SELECT RAISE(ABORT,'outcome write refused'); END`).run();
    const first = await execute(h, agent, actionId) as Times & {failure: unknown; receipt: unknown};
    await h.db.prepare('DROP TRIGGER refuse_outcome').run();

    assert.equal(provider.calls.length, 1, 'positive control: the provider was called once');
    assert.deepEqual([first.status, first.failure, first.receipt, first.completedAt], ['dispatching', null, null, null]);
    assert.notEqual(first.dispatchedAt, null);
    const stored = await h.db.prepare('SELECT status,receipt,completed_at FROM external_actions WHERE action_id=?').bind(actionId)
        .first<{status: string; receipt: string | null; completed_at: number | null}>();
    assert.deepEqual({...stored}, {status: 'dispatching', receipt: null, completed_at: null});
    const retried = await execute(h, agent, actionId) as Times;
    assert.deepEqual([retried.status, retried.completedAt, provider.calls.length], ['dispatching', null, 1], 'a retry while the plan is live sends nothing');

    // Hours later and past the plan's expiry: the row still reads as dispatching and nothing is sent again.
    const hours = (BigInt(6 * 3600_000) * 10000n).toString();
    await h.db.prepare(`UPDATE external_actions SET expires_at=expires_at-CAST(?1 AS INTEGER),dispatched_at=dispatched_at-CAST(?1 AS INTEGER),
        decided_at=decided_at-CAST(?1 AS INTEGER),created_at=created_at-CAST(?1 AS INTEGER) WHERE action_id=?2`).bind(hours, actionId).run();
    assert.equal((await (await h.mf.getWorker()).scheduled({cron: '*/5 * * * *', scheduledTime: Date.now()})).outcome, 'ok');
    for (const again of [await execute(h, agent, actionId), await read(h, agent, actionId), await execute(h, agent, actionId)] as (Times & {failure: unknown})[]) {
        assert.deepEqual([again.status, again.failure, again.completedAt], ['dispatching', null, null]);
        assert.notEqual(again.dispatchedAt, null);
    }
    assert.equal(provider.calls.length, 1);
}));

test('getExternalAction_AnotherAgent_Unavailable',() => scenario(async ({h, provider}) => {
    const {actionId} = await approved(h);
    const stranger = await h.register();
    assert.equal((await h.request(`/v1/external-actions/${actionId}`, stranger)).status, 404);
    assert.equal((await h.request(`/v1/external-actions/${actionId}/execute`, stranger, 'POST', {})).status, 404);
    assert.equal(provider.calls.length, 0);
}));

// Envoylet #95 reads remembered plans by ID long after its notices window and forgets one only on 404, so an action must
// stay readable, with its status, receipt and failure, after every retention sweep the cron runs (KoalaFacts/Weave#365).
test('getExternalAction_OldActionsAfterCronSweeps_StillReadWithTheirOutcome', () => scenario(async ({h, provider}) => {
    const done = await approved(h);
    await execute(h, done.agent, done.actionId);
    const lost = await approved(h);
    provider.respond = () => new Response('{}', {status: 502});
    await execute(h, lost.agent, lost.actionId);
    provider.respond = () => Response.json({reason: 'duplicate content'}, {status: 422});
    const refused = await approved(h);
    await execute(h, refused.agent, refused.actionId);
    const {agent, humanId} = await ownedAgent(h);
    const rejected = (await (await propose(h, agent)).json() as {actionId: string}).actionId;
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: rejected}), {ok: true});
    const waiting = (await (await propose(h, agent)).json() as {actionId: string}).actionId;
    const cases = [[done.agent, done.actionId, 'completed'], [lost.agent, lost.actionId, 'unknown'], [refused.agent, refused.actionId, 'failed'],
        [agent, rejected, 'rejected'], [agent, waiting, 'expired']] as const;
    const before = new Map<string, Record<string, unknown>>();
    for (const [as, actionId] of cases) before.set(actionId, await h.ok(`/v1/external-actions/${actionId}`, as));
    assert.equal(before.get(done.actionId)!.receipt, 'post-' + done.actionId, 'positive control: the receipt is recorded');

    const shift = (BigInt(400 * 24 * 3600_000) * 10000n).toString();
    await h.db.prepare(`UPDATE external_actions SET created_at=created_at-CAST(?1 AS INTEGER),expires_at=expires_at-CAST(?1 AS INTEGER),
        updated_at=updated_at-CAST(?1 AS INTEGER),decided_at=decided_at-CAST(?1 AS INTEGER),dispatched_at=dispatched_at-CAST(?1 AS INTEGER),
        completed_at=completed_at-CAST(?1 AS INTEGER)`).bind(shift).run();
    assert.equal((await (await h.mf.getWorker()).scheduled({cron: '*/5 * * * *', scheduledTime: Date.now()})).outcome, 'ok');

    for (const [as, actionId, status] of cases) {
        const response = await h.request(`/v1/external-actions/${actionId}`, as);
        assert.equal(response.status, 200, `${status} action still reads`);
        const now = await response.json() as Record<string, unknown>, then = before.get(actionId)!;
        assert.equal(now.status, status);
        for (const field of ['actionId', 'content', 'receipt', 'failure', 'targetAccount']) assert.equal(now[field], then[field], `${status} ${field}`);
    }
    assert.equal(provider.calls.length, 3, 'reading old actions sends nothing');
}));

test('proposeExternalAction_TwentyWaiting_429UntilOneIsExecutedRejectedOrExpired', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const ids: string[] = [];
    for (let i = 0; i < 20; i++) {
        const response = await propose(h, agent, `Post ${i}`);
        assert.equal(response.status, 200, `proposal ${i} is admitted`);
        ids.push((await response.json() as {actionId: string}).actionId);
    }
    const stored = async () => (await h.db.prepare('SELECT count(*) n FROM external_actions WHERE account_id=?').bind(agent.accountId).first<number>('n'));
    const refused = async (why: string) => {
        const response = await propose(h, agent, 'One too many');
        assert.equal(response.status, 429, why);
        assert.equal((await response.json() as {code: string}).code, 'capacity', why);
        assert.equal(response.headers.get('Retry-After'), null, `${why}: a waiting plan can leave at any time, so no wait is promised`);
    };
    const admitted = async (why: string) => {
        const response = await propose(h, agent, 'Room again');
        assert.equal(response.status, 200, why);
        return (await response.json() as {actionId: string}).actionId;
    };
    await refused('the 21st waiting plan');
    assert.deepEqual((await waiting(h, agent)).actions.map(a => a.actionId).sort(), [...ids].sort(), 'all 20 waiting plans are listed');
    assert.equal(await stored(), 20, 'a refusal writes nothing');
    const unknownCredential = await h.request('/v1/external-actions', agent, 'POST', {credentialRef: 'missing', operation: 'publish_text', content: 'Wrong account'});
    assert.equal(unknownCredential.status, 400, 'a plan that could never be admitted is refused for its fields first, so the caller fixes it instead of waiting for a place');
    assert.equal((await unknownCredential.json() as {field?: string}).field, 'credentialRef');
    const blank = await propose(h, agent, '   ');
    assert.equal(blank.status, 400, 'blank content is refused before the waiting-plan count');

    const other = await ownedAgent(h);
    assert.equal((await propose(h, other.agent)).status, 200, 'the limit is per agent');

    const shown = (await pendingFor(h, humanId)).find(action => action.actionId === ids[0])!;
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: ids[0], planDigest: shown.planDigest}), {ok: true});
    await refused('an approved plan that has not been executed still waits');
    assert.equal((await execute(h, agent, ids[0])).status, 'completed');
    const refill = await admitted('an executed plan frees its place');
    await refused('full again');

    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: ids[1]}), {ok: true});
    await admitted('a rejected plan frees its place');
    await refused('full again');

    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind(ticks(), refill).run();
    await admitted('an expired plan frees its place, without the cleanup sweep');
    await refused('full again');
    assert.equal(provider.calls.length, 1);
}));

const proposeKeyed = (h: Harness, as: AgentCredential, key: string, body: Record<string, unknown> = {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Episode 12 is live.'}) =>
    h.fetchAs(as, 'http://localhost/v1/external-actions', {method: 'POST', headers: {'Content-Type': 'application/json', 'Idempotency-Key': key}, body: JSON.stringify(body)});
const actionCount = (h: Harness) => h.db.prepare('SELECT count(*) n FROM external_actions').first<number>('n');

test('proposeExternalAction_SameKeySameBody_ReturnsTheOriginalActionInItsCurrentState', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const first = await proposeKeyed(h, agent, 'envoylet:post-1');
    assert.equal(first.status, 200, await first.clone().text());
    const original = await first.json() as {actionId: string; status: string; approvalUrl: string | null};
    const repeated = await (await proposeKeyed(h, agent, 'envoylet:post-1')).json() as typeof original;
    assert.deepEqual(repeated, original);
    assert.equal((await pendingFor(h, humanId)).length, 1);

    const [shown] = await pendingFor(h, humanId);
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: original.actionId, planDigest: shown.planDigest}), {ok: true});
    // The replay reads the action; it re-checks neither the credential nor capacity, so a later revocation still returns it.
    await h.db.prepare('UPDATE external_credentials SET revoked_at=CAST(? AS INTEGER) WHERE account_id=?').bind(ticks(), agent.accountId).run();
    const later = await proposeKeyed(h, agent, 'envoylet:post-1');
    assert.equal(later.status, 200, await later.clone().text());
    const current = await later.json() as typeof original;
    assert.equal(current.actionId, original.actionId);
    assert.equal(current.status, 'approved');
    assert.equal(current.approvalUrl, null);
    assert.equal(await actionCount(h), 1);
}));

test('proposeExternalAction_SameKeyDifferentBody_IdempotencyConflictWithNothingWritten', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    await setCredential(h, agent.accountId);
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
        VALUES(?,'linkedin-other','test-http','rev-1','urn:li:person:alpha','Desk Alpha',CAST(? AS INTEGER),CAST(? AS INTEGER))`).bind(agent.accountId, ticks(), ticks()).run();
    assert.equal((await proposeKeyed(h, agent, 'k1')).status, 200);
    for (const body of [{credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Episode 13 is live.'},
        {credentialRef: 'linkedin-other', operation: 'publish_text', content: 'Episode 12 is live.'}]) {
        const refused = await proposeKeyed(h, agent, 'k1', body);
        assert.equal(refused.status, 409);
        assert.deepEqual(await refused.json(), {status: 409, title: 'State conflict', code: 'conflict', field: 'action', reason: 'idempotency-conflict'});
    }
    assert.equal(await actionCount(h), 1);
}));

test('proposeExternalAction_KeysArePerAgentAndAbsentKeysDoNotDeduplicate', () => scenario(async ({h}) => {
    const one = await ownedAgent(h), two = await ownedAgent(h);
    const a = await (await proposeKeyed(h, one.agent, 'shared')).json() as {actionId: string};
    const b = await (await proposeKeyed(h, two.agent, 'shared')).json() as {actionId: string};
    assert.notEqual(a.actionId, b.actionId);
    // Another agent cannot read the first agent's action through its key: it got its own.
    assert.equal((await h.request(`/v1/external-actions/${a.actionId}`, two.agent)).status, 404);
    const c = await (await propose(h, one.agent)).json() as {actionId: string};
    const d = await (await propose(h, one.agent)).json() as {actionId: string};
    assert.notEqual(c.actionId, d.actionId);
    assert.equal(await actionCount(h), 4);
}));

test('proposeExternalAction_KeyedReplayWhileTwentyWait_ReturnsTheActionNot429', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const first = await proposeKeyed(h, agent, 'retry-me');
    assert.equal(first.status, 200, await first.clone().text());
    const {actionId} = await first.json() as {actionId: string};
    for (let i = 1; i < 20; i++) assert.equal((await propose(h, agent, `Post ${i}`)).status, 200, `proposal ${i} is admitted`);
    const full = await proposeKeyed(h, agent, 'new-key', {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'One too many'});
    assert.equal(full.status, 429, 'a new key still counts against the waiting-plan limit');
    // A retry whose first answer was lost must find its own plan, even though that plan is what fills the last place.
    const replay = await proposeKeyed(h, agent, 'retry-me');
    assert.equal(replay.status, 200, await replay.clone().text());
    assert.equal((await replay.json() as {actionId: string}).actionId, actionId);
    assert.equal(await actionCount(h), 20);
}));

test('proposeExternalAction_RefusedKeyedProposal_LeavesTheKeyUnused', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const unknownCredential = await proposeKeyed(h, agent, 'fix-and-retry', {credentialRef: 'missing', operation: 'publish_text', content: 'Episode 12 is live.'});
    assert.equal(unknownCredential.status, 400);
    const blank = await proposeKeyed(h, agent, 'fix-and-retry', {credentialRef: 'linkedin-main', operation: 'publish_text', content: '   '});
    assert.equal(blank.status, 400);
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_action_keys').first<number>('n'), 0, 'a refusal records no key');
    // The corrected request is a different request; it is admitted under the same key instead of conflicting.
    const fixed = await proposeKeyed(h, agent, 'fix-and-retry');
    assert.equal(fixed.status, 200, await fixed.clone().text());
    const {actionId} = await fixed.json() as {actionId: string};
    assert.equal((await (await proposeKeyed(h, agent, 'fix-and-retry')).json() as {actionId: string}).actionId, actionId);
    assert.equal(await actionCount(h), 1);
}));

test('proposeExternalAction_InvalidIdempotencyKey_RejectedBeforeAnyWrite', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    // An empty header does not reach the Worker; the parser refuses it all the same.
    assert.throws(() => proposalKey('', 'Idempotency-Key'), {status: 400});
    for (const key of ['has space', 'a'.repeat(129), 'k1, k2', 'key/slash']) {
        const refused = await proposeKeyed(h, agent, key);
        assert.equal(refused.status, 400, JSON.stringify(key));
        assert.equal((await refused.json() as {field: string}).field, 'Idempotency-Key');
    }
    assert.equal((await proposeKeyed(h, agent, 'a'.repeat(128))).status, 200);
    assert.equal(await actionCount(h), 1);
}));

test('proposeExternalAction_ConcurrentSameKey_OnePlan', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const responses = await Promise.all([1, 2, 3].map(() => proposeKeyed(h, agent, 'race')));
    const ids = new Set<string>();
    for (const response of responses) {
        assert.equal(response.status, 200, await response.clone().text());
        ids.add((await response.json() as {actionId: string}).actionId);
    }
    assert.equal(ids.size, 1);
    assert.equal((await pendingFor(h, humanId)).length, 1);
}));

test('proposeExternalAction_CrossAccountKeyLink_FailsClosed', () => scenario(async ({h}) => {
    const {agent: first} = await ownedAgent(h);
    const {agent: second} = await ownedAgent(h);
    const initial = await proposeKeyed(h, second, 'mislinked');
    assert.equal(initial.status, 200);
    const {actionId} = await initial.json() as {actionId: string};
    // The separate account and action FKs permit a structurally valid cross-account link.
    // A corrupt/mislinked key must never return another agent's private proposal.
    await h.db.prepare('UPDATE external_action_keys SET account_id=? WHERE action_id=?').bind(first.accountId, actionId).run();
    const replay = await proposeKeyed(h, first, 'mislinked');
    assert.equal(replay.status, 503, 'a mislinked key must fail closed rather than expose another agent\'s action');
    assert.equal((await replay.json() as {code: string}).code, 'storage');
}));

// Envoylet #153 shows "Approved"/"Rejected" from decidedAt and "Result recorded by Weave" from completedAt (KoalaFacts/Weave#365):
// decidedAt is set only by the owner's approve or reject and never moves afterwards; completedAt is set exactly when the
// action reaches completed, failed (including a refusal before dispatch) or unknown, and stays null otherwise.
type Times = {status: string; decidedAt: string | null; dispatchedAt: string | null; completedAt: string | null};
const read = async (h: Harness, as: AgentCredential, actionId: string) => await h.ok(`/v1/external-actions/${actionId}`, as) as Times;
const pastExpiry = (h: Harness, actionId: string) => h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?')
    .bind((BigInt(ticks()) - 1n).toString(), actionId).run();
const notBefore = (later: string, earlier: string) => assert.ok(Date.parse(later) >= Date.parse(earlier), `${later} is before ${earlier}`);

test('externalActionTimes_PendingAndExpiredUndecided_NoDecisionAndNoResult', () => scenario(async ({h, provider}) => {
    const {agent} = await ownedAgent(h);
    const {actionId} = await (await propose(h, agent)).json() as {actionId: string};
    for (const seen of [await read(h, agent, actionId), await execute(h, agent, actionId) as Times])
        assert.deepEqual([seen.status, seen.decidedAt, seen.dispatchedAt, seen.completedAt], ['pending', null, null, null]);
    await pastExpiry(h, actionId);
    for (const seen of [await read(h, agent, actionId), await execute(h, agent, actionId) as Times])
        assert.deepEqual([seen.status, seen.decidedAt, seen.dispatchedAt, seen.completedAt], ['expired', null, null, null]);
    assert.equal(provider.calls.length, 0);
}));

test('externalActionTimes_Approved_DecidedAtSetOnceAndKeptThroughExecute', () => scenario(async ({h}) => {
    const {agent, actionId} = await approved(h);
    const decided = await read(h, agent, actionId);
    assert.deepEqual([decided.status, decided.dispatchedAt, decided.completedAt], ['approved', null, null]);
    assert.notEqual(decided.decidedAt, null);
    const done = await execute(h, agent, actionId) as Times;
    assert.equal(done.status, 'completed');
    assert.equal(done.decidedAt, decided.decidedAt);
    notBefore(done.dispatchedAt!, decided.decidedAt!);
    notBefore(done.completedAt!, done.dispatchedAt!);
    const again = await read(h, agent, actionId);
    assert.deepEqual([again.decidedAt, again.dispatchedAt, again.completedAt], [done.decidedAt, done.dispatchedAt, done.completedAt]);
    assert.deepEqual((await execute(h, agent, actionId) as Times).completedAt, done.completedAt);
}));

test('externalActionTimes_Rejected_DecidedAtSetAndNoResultEvenPastExpiry', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const {actionId} = await (await propose(h, agent)).json() as {actionId: string};
    await pastExpiry(h, actionId);
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: actionId}), {ok: true});
    const rejected = await read(h, agent, actionId);
    assert.deepEqual([rejected.status, rejected.dispatchedAt, rejected.completedAt], ['rejected', null, null]);
    assert.notEqual(rejected.decidedAt, null);
    const again = await execute(h, agent, actionId) as Times;
    assert.deepEqual([again.status, again.decidedAt, again.completedAt], ['rejected', rejected.decidedAt, null]);
    assert.equal(provider.calls.length, 0);
}));

test('externalActionTimes_ApprovedThenExpired_DecidedAtKeptAndNoResult', () => scenario(async ({h, provider}) => {
    const {agent, actionId} = await approved(h);
    const decidedAt = (await read(h, agent, actionId)).decidedAt;
    await pastExpiry(h, actionId);
    const seen = await execute(h, agent, actionId) as Times;
    assert.deepEqual([seen.status, seen.decidedAt, seen.dispatchedAt, seen.completedAt], ['expired', decidedAt, null, null]);
    assert.notEqual(decidedAt, null);
    assert.equal(provider.calls.length, 0);
}));

test('externalActionTimes_RefusedBeforeDispatch_CompletedAtSetWithoutDispatch', () => scenario(async ({h, provider}) => {
    const {agent, humanId, actionId} = await approved(h);
    const decidedAt = (await read(h, agent, actionId)).decidedAt!;
    await h.db.prepare('UPDATE humans SET frozen_at=CAST(? AS INTEGER) WHERE human_id=?').bind(ticks(), humanId).run();
    const refused = await execute(h, agent, actionId) as Times & {failure: string};
    assert.deepEqual([refused.status, refused.failure, refused.decidedAt, refused.dispatchedAt], ['failed', 'owner-frozen', decidedAt, null]);
    notBefore(refused.completedAt!, decidedAt);
    assert.equal((await read(h, agent, actionId)).completedAt, refused.completedAt);
    assert.equal((await execute(h, agent, actionId) as Times).completedAt, refused.completedAt);
    assert.equal(provider.calls.length, 0);
}));

// Envoylet #154 shows dispatchedAt as "Weave started sending": it is stamped when the action enters dispatching, before the
// provider call, and the outcome write never moves it. The provider answers late so the two stamps are distinguishable.
const late = (respond: (call: Call) => Response) => async (call: Call) => { await new Promise(resolve => setTimeout(resolve, 25)); return respond(call); };
for (const [name, status, respond] of [['Completed', 'completed', completed],
    ['ProviderRejected', 'failed', () => Response.json({reason: 'duplicate content'}, {status: 422})],
    ['OutcomeUnknown', 'unknown', () => new Response('{}', {status: 502})]] as const)
    test(`externalActionTimes_${name}_DispatchedAtBeforeProviderAnswerAndBothKept`, () => scenario(async ({h, provider}) => {
        const {agent, actionId} = await approved(h);
        const decidedAt = (await read(h, agent, actionId)).decidedAt!;
        provider.respond = late(respond);
        const result = await execute(h, agent, actionId) as Times;
        assert.deepEqual([result.status, result.decidedAt], [status, decidedAt]);
        notBefore(result.dispatchedAt!, decidedAt);
        assert.ok(Date.parse(result.completedAt!) > Date.parse(result.dispatchedAt!),
            `dispatchedAt ${result.dispatchedAt} must be stamped before the provider answered (completedAt ${result.completedAt})`);
        provider.respond = completed;
        for (const again of [await read(h, agent, actionId), await execute(h, agent, actionId) as Times])
            assert.deepEqual([again.status, again.decidedAt, again.dispatchedAt, again.completedAt],
                [status, decidedAt, result.dispatchedAt, result.completedAt]);
        assert.equal(provider.calls.length, 1);
    }));

// A plan's expiry is exact on one pinned clock: it is live until the tick before its stored expiry and expired from that tick,
// for execute, for the owner's confirm, for the owner page's list and for the 20-waiting-plans count alike.
const directCaller = (accountId: string): ActionCaller => ({accountId, check: async () => {}});
const storedExpiry = async (h: Harness, actionId: string) =>
    BigInt((await h.db.prepare('SELECT CAST(expires_at AS TEXT) e FROM external_actions WHERE action_id=?').bind(actionId).first<string>('e'))!);
const storedState = (h: Harness, actionId: string) =>
    h.db.prepare('SELECT status,CAST(decided_at AS TEXT) decided_at,CAST(dispatched_at AS TEXT) dispatched_at FROM external_actions WHERE action_id=?').bind(actionId)
        .first<{status: string; decided_at: string | null; dispatched_at: string | null}>();

test('executeExternalAction_AtTheExactExpiryTick_ExpiredAndUnsentWhileOneTickEarlierSends', () => scenario(async ({h}) => {
    const {agent, actionId} = await approved(h);
    const expiry = await storedExpiry(h, actionId);
    const sent: string[] = [];
    const realFetch = globalThis.fetch;
    // A direct call runs in this process, so the provider's request is caught here; every other request passes through.
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = input instanceof Request ? input.url : String(input);
        if (url !== endpoint) return realFetch(input, init);
        sent.push(url);
        return Response.json({externalId: 'post-exact'});
    }) as typeof fetch;
    try {
        const atExpiry = await executeExternalAction(environment(h), directCaller(agent.accountId), actionId, () => expiry);
        assert.equal(atExpiry.status, 'expired');
        assert.equal(sent.length, 0, 'nothing is sent at the expiry tick');
        const kept = await storedState(h, actionId);
        assert.equal(kept!.status, 'approved', 'an expired execute writes nothing');
        assert.equal(kept!.dispatched_at, null);

        const before = await executeExternalAction(environment(h), directCaller(agent.accountId), actionId, () => expiry - 1n);
        assert.equal(before.status, 'completed', 'one tick before the expiry the plan is still live');
        assert.equal(before.receipt, 'post-exact');
        assert.equal(sent.length, 1);
    } finally { globalThis.fetch = realFetch; }
}));

test('confirmExternalAction_AtTheExactExpiryTick_ExpiredWhileOneTickEarlierApproves', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const [shown] = await pendingFor(h, humanId);
    const expiry = await storedExpiry(h, action.actionId);
    const confirmAt = (at: bigint) => ownerAction(environment(h), {humanId, authAt: Number(at / 10000n), kind: 'confirm-action',
        requestId: action.actionId, planDigest: shown.planDigest} as OwnerAction, () => at);

    assert.deepEqual(await confirmAt(expiry), {ok: false, reason: 'expired'});
    assert.equal((await storedState(h, action.actionId))!.status, 'pending', 'a refused confirm writes nothing');
    assert.deepEqual(await confirmAt(expiry - 1n), {ok: true});
    const decided = await storedState(h, action.actionId);
    assert.equal(decided!.status, 'approved');
    assert.equal(decided!.decided_at, (expiry - 1n).toString(), 'the decision is stamped with the same clock');
}));

test('ownerOverview_PendingPlanAtItsExactExpiryTick_NoLongerListed', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const expiry = await storedExpiry(h, action.actionId);
    const listedAt = async (at: bigint) => {
        const overview = await ownerOverview(environment(h), humanId, null, () => at);
        assert.equal(overview.frozen, false);
        return (overview as Extract<typeof overview, {frozen: false}>).externalActions.map(listed => listed.actionId);
    };
    assert.deepEqual(await listedAt(expiry - 1n), [action.actionId], 'listed until the tick before its expiry');
    assert.deepEqual(await listedAt(expiry), [], 'not offered for approval from its expiry tick');
}));

test('proposeExternalAction_WaitingPlansAtTheirExactExpiryTick_NoLongerCountTowardTwenty', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    for (let i = 0; i < 20; i++) assert.equal((await propose(h, agent, `Post ${i}`)).status, 200, `proposal ${i} is admitted`);
    const expiry = BigInt(ticks()) + 600_000_000n;
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE account_id=?').bind(expiry.toString(), agent.accountId).run();
    const input = {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'One more'};
    const proposeAt = (at: bigint) => proposeExternalAction(environment(h), directCaller(agent.accountId), input, null, () => at);

    await assert.rejects(proposeAt(expiry - 1n), (error: unknown) => (error as Problem).status === 429 && (error as Problem).code === 'capacity',
        'twenty plans still wait one tick before their expiry');
    assert.equal(await actionCount(h), 20, 'a refusal writes nothing');
    const admitted = await proposeAt(expiry);
    assert.equal(admitted.status, 'pending', 'at their expiry tick they no longer wait');
    assert.equal(admitted.createdAt, apiTime(expiry.toString()));
    assert.equal(admitted.expiresAt, apiTime((expiry + actionLifetimeTicks).toString()), 'the new plan expires one lifetime after the same tick');
}));

type Listed = {actions: {actionId: string; status: string; content: string; approvalUrl: string | null; receipt: string | null}[]};
const waiting = async (h: Harness, as: AgentCredential) => {
    const response = await h.request('/v1/external-actions', as);
    assert.equal(response.status, 200, await response.clone().text());
    assert.equal(response.headers.get('Cache-Control'), 'no-store');
    return await response.json() as Listed;
};

// An app that lost a propose answer (sent without an Idempotency-Key) or a remembered action ID finds the plan here
// while the owner can still approve it and the agent can still execute it (KoalaFacts/Weave#365, propose-idempotency option 2).
test('listWaitingExternalActions_PendingAndApprovedOfThisAgentOnly_OldestFirstUntilSettledOrExpired', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    assert.deepEqual(await waiting(h, agent), {actions: []}, 'an agent with no plans gets an empty list');

    const ids: string[] = [];
    for (const content of ['First', 'Second', 'Third', 'Fourth']) ids.push((await (await propose(h, agent, content)).json() as {actionId: string}).actionId);
    // Pin creation ticks one apart in reverse proposal order, so the order checked is the stored creation time, not insertion.
    const base = BigInt(ticks()) - 1_000_000n;
    for (const [i, id] of ids.entries()) await h.db.prepare('UPDATE external_actions SET created_at=CAST(? AS INTEGER) WHERE action_id=?').bind((base + BigInt(ids.length - i)).toString(), id).run();
    const other = await ownedAgent(h);
    const strangerPlan = (await (await propose(h, other.agent, 'Not yours')).json() as {actionId: string}).actionId;

    let listed = await waiting(h, agent);
    assert.deepEqual(listed.actions.map(a => a.content), ['Fourth', 'Third', 'Second', 'First'], 'oldest first, and only this agent\'s plans');
    assert.ok(listed.actions.every(a => a.status === 'pending' && a.receipt === null));
    assert.deepEqual((await waiting(h, other.agent)).actions.map(a => a.actionId), [strangerPlan]);
    const read = await h.ok(`/v1/external-actions/${ids[0]}`, agent);
    assert.deepEqual(listed.actions.find(a => a.actionId === ids[0]), read, 'each entry is the same view as reading the action by its ID');

    const shown = (await pendingFor(h, humanId)).find(action => action.actionId === ids[0])!;
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: ids[0], planDigest: shown.planDigest}), {ok: true});
    listed = await waiting(h, agent);
    assert.equal(listed.actions.find(a => a.actionId === ids[0])?.status, 'approved', 'an approved plan still waits for its execute');
    assert.equal(listed.actions.find(a => a.actionId === ids[0])?.approvalUrl, null);

    assert.equal((await execute(h, agent, ids[0])).status, 'completed');
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: ids[1]}), {ok: true});
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind(ticks(), ids[2]).run();
    assert.deepEqual((await waiting(h, agent)).actions.map(a => a.content), ['Fourth'], 'completed, rejected and expired plans are read by ID, not listed');
    assert.equal(provider.calls.length, 1, 'listing sends nothing');
}));

test('listWaitingExternalActions_RevokedKey_Unauthenticated', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    await propose(h, agent);
    await h.db.prepare('UPDATE agent_keys SET revoked_at=CAST(? AS INTEGER) WHERE kid=?').bind(ticks(), agent.kid).run();
    const response = await h.request('/v1/external-actions', agent);
    assert.equal(response.status, 401);
    assert.equal((await response.json() as {code: string}).code, 'unauthenticated');
}));

test('listWaitingExternalActions_PlanAtItsExactExpiryTick_NoLongerListed', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const expiry = await storedExpiry(h, action.actionId);
    const caller: ActionCaller = {accountId: agent.accountId, check: async () => {}};
    const listedAt = async (at: bigint) => (await listWaitingExternalActions(environment(h), caller, () => at)).actions.map(a => a.actionId + ':' + a.status);
    assert.deepEqual(await listedAt(expiry - 1n), [action.actionId + ':pending'], 'listed until the tick before its expiry');
    assert.deepEqual(await listedAt(expiry), [], 'not listed from its expiry tick, never as expired');
}));

// The owner's pending actions are every agent's, named, whichever page of agents the overview shows.
test('ownerOverview_ActionOfAgentBeyondFirstPage_ListedWithItsName', () => scenario(async ({h}) => {
    const {agent: first, humanId} = await ownedAgent(h);
    const many = environment(h, {OWNED_AGENTS_PER_HUMAN: '50', REGISTRATION_PER_IP_PER_HOUR: '1000', REGISTRATION_GLOBAL_PER_HOUR: '1000'});
    for (let i = 0; i < 25; i++) assert.equal((await createOwnedAgent(many, {humanId, displayName: 'filler ' + i, sourceIp: '203.0.113.7'})).ok, true);
    const late = await h.register({displayName: 'Late desk'});
    await h.db.prepare('UPDATE accounts SET human_id=?,created_at=created_at+1000000000 WHERE account_id=?').bind(humanId, late.accountId).run();
    await setCredential(h, late.accountId);
    const response = await propose(h, late, 'From the last page.');
    assert.equal(response.status, 200, await response.clone().text());
    const {actionId} = await response.json() as {actionId: string};
    const overview = await ownerOverview(environment(h), humanId);
    if (overview.frozen) throw Error('frozen');
    assert.ok(!overview.agents.some(agent => agent.agentId === late.accountId), 'the proposing agent is not on the first page');
    assert.ok(overview.agents.some(agent => agent.agentId === first.accountId));
    const listed = overview.externalActions.find(action => action.actionId === actionId);
    assert.equal(listed?.agentDisplayName, 'Late desk');
}));

// The owner page lists what became of the owner's own actions in the last 7 days, newest change first, and nobody else's.
test('ownerOverview_RecentActions_SettledInTheWindowNewestFirstOwnAgentsOnly', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const ids: string[] = [];
    for (let i = 0; i < 4; i++) {
        const response = await propose(h, agent, 'Recent plan ' + i);
        assert.equal(response.status, 200, await response.clone().text());
        ids.push((await response.json() as {actionId: string}).actionId);
    }
    const recent = async (who: string, clock?: () => bigint) => {
        const overview = await ownerOverview(environment(h), who, null, clock);
        return overview.frozen ? null : overview.recentActions;
    };
    assert.deepEqual(await recent(humanId), [], 'pending plans are not recent outcomes');
    const now = BigInt((await h.db.prepare('SELECT CAST(MAX(updated_at) AS TEXT) t FROM external_actions').first<string>('t'))!) + 100n;
    const set = (id: string, status: string, updated: bigint, failure: string | null = null) => h.db.prepare(`UPDATE external_actions SET status=?,failure=?,
        decided_at=CASE WHEN ?='pending' THEN NULL ELSE created_at END,decided_by=CASE WHEN ?='pending' THEN NULL ELSE ? END,
        dispatched_at=CASE WHEN ? IN ('dispatching','completed','unknown') THEN created_at END,updated_at=CAST(? AS INTEGER) WHERE action_id=?`)
        .bind(status, failure, status, status, humanId, status, updated.toString(), id).run();
    await set(ids[0]!, 'completed', now - 30n);
    await set(ids[1]!, 'failed', now - 10n, 'provider-refused');
    await set(ids[2]!, 'rejected', now - recentActionWindowTicks, null);
    // A plan left pending is listed from its expiry, by that expiry.
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind((now - 20n).toString(), ids[3]!).run();
    const listed = await recent(humanId, () => now);
    assert.deepEqual(listed?.map(action => [action.actionId, action.status, action.failure]),
        [[ids[1], 'failed', 'provider-refused'], [ids[3], 'expired', null], [ids[0], 'completed', null]], 'newest change first; a change exactly 7 days old is out');
    assert.equal(listed?.[0]?.changedAt, apiTime((now - 10n).toString()));
    assert.equal(listed?.[1]?.changedAt, apiTime((now - 20n).toString()));
    assert.equal(listed?.[0]?.agentId, agent.accountId);
    await set(ids[2]!, 'rejected', now - recentActionWindowTicks + 1n);
    assert.equal((await recent(humanId, () => now))?.length, 4, 'one tick inside the window is listed');
    // A change after the read's own clock is not listed yet; a pending plan one tick before its expiry is still waiting.
    assert.deepEqual((await recent(humanId, () => now - 21n))?.map(action => action.actionId), [ids[0], ids[2]]);
    // A plan approved 8 days ago that expired 5 ticks ago unsent is listed as expired, by its expiry.
    await set(ids[2]!, 'approved', now - recentActionWindowTicks - 1n);
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind((now - 5n).toString(), ids[2]!).run();
    const approved = await recent(humanId, () => now);
    assert.deepEqual(approved?.[0] && [approved[0].actionId, approved[0].status, approved[0].changedAt], [ids[2], 'expired', apiTime((now - 5n).toString())]);
    const {humanId: stranger} = await ownedAgent(h);
    assert.deepEqual(await recent(stranger, () => now), [], 'another human sees none of it');
    await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(stranger, agent.accountId).run();
    assert.deepEqual(await recent(humanId, () => now), [], 'an agent passed to another human is no longer listed to the first');
    assert.equal((await recent(stranger, () => now))?.length, 4);
}));

// At most 20 are listed, the newest.
test('ownerOverview_RecentActions_AtMostTheNewestTwenty', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const ids: string[] = [];
    for (let i = 0; i <= recentActionsShownToOwner; i++) {
        await h.db.prepare("UPDATE external_actions SET status='rejected',decided_at=created_at,decided_by=? WHERE account_id=? AND status='pending'").bind(humanId, agent.accountId).run();
        const response = await propose(h, agent, 'Plan ' + i);
        assert.equal(response.status, 200, await response.clone().text());
        ids.push((await response.json() as {actionId: string}).actionId);
    }
    const now = BigInt((await h.db.prepare('SELECT CAST(MAX(updated_at) AS TEXT) t FROM external_actions').first<string>('t'))!) + 100n;
    for (const [i, id] of ids.entries()) await h.db.prepare("UPDATE external_actions SET status='rejected',decided_at=created_at,decided_by=?,updated_at=CAST(? AS INTEGER) WHERE action_id=?")
        .bind(humanId, (now - 50n + BigInt(i)).toString(), id).run();
    const overview = await ownerOverview(environment(h), humanId, null, () => now);
    assert.equal(overview.frozen, false);
    const listed = (overview as {recentActions: {actionId: string}[]}).recentActions.map(action => action.actionId);
    assert.deepEqual(listed, ids.slice(1).reverse());
}));

// The page for one action tells its owner what became of an action no longer waiting, and nobody else anything.
test('ownerPendingAction_OwnActionNoLongerPending_SettledWithStatusAndFailureOnlyForItsOwner', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const response = await propose(h, agent, 'A plan that settles.');
    assert.equal(response.status, 200, await response.clone().text());
    const {actionId} = await response.json() as {actionId: string};
    const pending = await ownerPendingAction(environment(h), humanId, actionId);
    assert.equal((pending as {action: {actionId: string} | null}).action?.actionId, actionId);
    assert.equal((pending as {settled: unknown}).settled, null, 'a pending action is offered for decision, not described as settled');
    // What the action was is the same whatever became of it (its own test covers the fields).
    const about = {agentId: agent.accountId, agentDisplayName: null, provider: 'test-http', operation: 'publish_text', targetAccount: 'urn:li:person:alpha',
        targetDisplay: 'Desk Alpha', content: 'A plan that settles.'};
    const {humanId: stranger} = await ownedAgent(h);
    for (const [status, failure] of [['approved', null], ['rejected', null], ['dispatching', null], ['completed', null], ['failed', 'owner-changed'], ['unknown', null]] as const) {
        // A receipt is stored on every row here so that only a sent action shows one.
        await h.db.prepare(`UPDATE external_actions SET status=?,failure=?,receipt='posted:r-1',decided_at=created_at,decided_by=?,
            dispatched_at=CASE WHEN ? IN ('dispatching','completed','unknown') THEN created_at END WHERE action_id=?`).bind(status, failure, humanId, status, actionId).run();
        const receipt = status === 'completed' ? 'posted:r-1' : null;
        assert.deepEqual(await ownerPendingAction(environment(h), humanId, actionId), {frozen: false, action: null, settled: {status, failure, receipt, publication: null, about}}, status);
        assert.deepEqual(await ownerPendingAction(environment(h), stranger, actionId), {frozen: false, action: null, settled: null}, 'another human learns nothing of ' + status);
    }
    // Pending or approved past its expiry reads expired, exactly from the expiry tick, as every other read does.
    const expiry = BigInt((await h.db.prepare('SELECT CAST(expires_at AS TEXT) e FROM external_actions WHERE action_id=?').bind(actionId).first<string>('e'))!);
    for (const status of ['pending', 'approved'] as const) {
        await h.db.prepare(`UPDATE external_actions SET status=?,failure=NULL,dispatched_at=NULL,decided_at=CASE WHEN ?='pending' THEN NULL ELSE created_at END,
            decided_by=CASE WHEN ?='pending' THEN NULL ELSE ? END WHERE action_id=?`).bind(status, status, status, humanId, actionId).run();
        assert.deepEqual(await ownerPendingAction(environment(h), humanId, actionId, () => expiry), {frozen: false, action: null, settled: {status: 'expired', failure: null, receipt: null, publication: null, about}}, status);
        const before = await ownerPendingAction(environment(h), humanId, actionId, () => expiry - 1n);
        assert.deepEqual((before as {settled: unknown}).settled, status === 'pending' ? null : {status: 'approved', failure: null, receipt: null, publication: null, about}, status + ' one tick earlier');
    }
    // A sent action whose effect is tracked after sending carries the publication's state, only for its owner.
    await h.db.prepare("UPDATE external_actions SET status='completed',failure=NULL,dispatched_at=created_at WHERE action_id=?").bind(actionId).run();
    await h.db.prepare(`INSERT INTO external_publications(action_id,account_id,state,url,error,due_at,checks,next_check_at,updated_at,checked_at)
        SELECT action_id,account_id,'published','https://example.test/p/1',NULL,created_at,1,NULL,created_at,created_at FROM external_actions WHERE action_id=?`).bind(actionId).run();
    const sent = await ownerPendingAction(environment(h), humanId, actionId) as {settled: {receipt: string | null; publication: {state: string; url: string | null} | null}};
    assert.equal(sent.settled.receipt, 'posted:r-1');
    assert.deepEqual([sent.settled.publication?.state, sent.settled.publication?.url], ['published', 'https://example.test/p/1']);
    assert.deepEqual(await ownerPendingAction(environment(h), stranger, actionId), {frozen: false, action: null, settled: null});
    await h.db.prepare("UPDATE external_actions SET status='failed',failure='owner-changed' WHERE action_id=?").bind(actionId).run();
    assert.equal((await ownerPendingAction(environment(h), humanId, actionId) as {settled: {publication: unknown}}).settled.publication, null, 'only a sent action reads its publication');
    // An action of an agent that passed to another human is no longer this human's to read.
    await h.db.prepare("UPDATE external_actions SET status='completed',failure=NULL,receipt=NULL,dispatched_at=created_at WHERE action_id=?").bind(actionId).run();
    await h.db.prepare('DELETE FROM external_publications WHERE action_id=?').bind(actionId).run();
    await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(stranger, agent.accountId).run();
    assert.deepEqual(await ownerPendingAction(environment(h), humanId, actionId), {frozen: false, action: null, settled: null});
    assert.deepEqual(await ownerPendingAction(environment(h), stranger, actionId), {frozen: false, action: null, settled: {status: 'completed', failure: null, receipt: null, publication: null, about}});
}));

// The page for one action names what the settled action was: its agent, operation, the account the plan bound and the content.
test('ownerPendingAction_OwnSettledAction_DescribesThePlanAndTheAccountNameOnlyWhileTheCredentialStillPointsThere', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    await h.db.prepare("UPDATE accounts SET display_name='Release bot' WHERE account_id=?").bind(agent.accountId).run();
    const response = await propose(h, agent, 'Episode <13> is live.');
    assert.equal(response.status, 200, await response.clone().text());
    const {actionId} = await response.json() as {actionId: string};
    await h.db.prepare("UPDATE external_actions SET status='rejected',decided_at=created_at,decided_by=? WHERE action_id=?").bind(humanId, actionId).run();
    const credential = (await h.db.prepare("SELECT target_account,target_display FROM external_credentials WHERE account_id=? AND credential_ref='linkedin-main'")
        .bind(agent.accountId).first<{target_account: string; target_display: string}>())!;
    const read = async () => (await ownerPendingAction(environment(h), humanId, actionId) as {settled: {about: unknown}}).settled.about;
    assert.deepEqual(await read(), {agentId: agent.accountId, agentDisplayName: 'Release bot', provider: 'test-http', operation: 'publish_text',
        targetAccount: credential.target_account, targetDisplay: credential.target_display, content: 'Episode <13> is live.'});
    // A revoked credential that still names the same account keeps its name.
    await h.db.prepare("UPDATE external_credentials SET revoked_at=updated_at WHERE account_id=?").bind(agent.accountId).run();
    assert.equal((await read() as {targetDisplay: string | null}).targetDisplay, credential.target_display);
    // Re-pointed at another account, the credential's name is not this plan's: only the account the plan bound is shown.
    await h.db.prepare("UPDATE external_credentials SET target_account='someone-else',target_display='Someone else' WHERE account_id=?").bind(agent.accountId).run();
    assert.deepEqual(await read() as {targetAccount: string; targetDisplay: string | null}, {agentId: agent.accountId, agentDisplayName: 'Release bot', provider: 'test-http',
        operation: 'publish_text', targetAccount: credential.target_account, targetDisplay: null, content: 'Episode <13> is live.'});
    // A credential that no longer exists names nothing either.
    await h.db.prepare('DELETE FROM external_credentials WHERE account_id=?').bind(agent.accountId).run();
    assert.equal((await read() as {targetDisplay: string | null}).targetDisplay, null);
    const {humanId: stranger} = await ownedAgent(h);
    assert.deepEqual(await ownerPendingAction(environment(h), stranger, actionId), {frozen: false, action: null, settled: null});
}));

// The page for one action reads it by ID: an action past the overview's 200 oldest can still be decided from its link.
test('ownerPendingAction_ActionBeyondTheOverviewCap_FoundByIdForItsOwnerOnly', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const response = await propose(h, agent, 'The newest plan.');
    assert.equal(response.status, 200, await response.clone().text());
    const {actionId} = await response.json() as {actionId: string};
    await h.db.prepare(`WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<205)
        INSERT INTO external_actions(action_id,account_id,provider,contract_revision,operation,content,credential_ref,credential_revision,target_account,plan_digest,status,created_at,expires_at,updated_at)
        SELECT lower(hex(randomblob(4)))||'-0000-4000-8000-'||printf('%012d',i),account_id,provider,contract_revision,operation,content,credential_ref,credential_revision,
        target_account,plan_digest,status,created_at-1000-i,expires_at,updated_at FROM external_actions,n WHERE action_id=?`).bind(actionId).run();
    const overview = await ownerOverview(environment(h), humanId);
    if (overview.frozen) throw Error('frozen');
    assert.equal(overview.externalActions.length, 200);
    assert.ok(!overview.externalActions.some(action => action.actionId === actionId), 'the newest is past the overview cap');
    const found = await ownerPendingAction(environment(h), humanId, actionId);
    assert.equal(found.frozen, false);
    assert.equal((found as {action: {actionId: string} | null}).action?.actionId, actionId);
    const {humanId: stranger} = await ownedAgent(h);
    assert.deepEqual(await ownerPendingAction(environment(h), stranger, actionId), {frozen: false, action: null, settled: null});
    assert.deepEqual(await ownerPendingAction(environment(h), humanId, 'not-an-id'), {frozen: false, action: null, settled: null});
    await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(humanId).run();
    assert.deepEqual(await ownerPendingAction(environment(h), humanId, actionId), {frozen: true});
}));
