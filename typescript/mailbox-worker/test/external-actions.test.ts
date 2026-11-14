import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { ownerAction, ownerOverview, reauthWindowMs, type OwnerAction } from '../src/owner-actions.ts';
import { approvalOrigin, planDigest, proposalKey } from '../src/external-actions.ts';
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

test('confirmExternalAction_DigestOrCredentialChanged_PlanChanged', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    const [shown] = await pendingFor(h, humanId);
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: 'A'.repeat(43)}), {ok: false, reason: 'plan-changed'});
    await setCredential(h, agent.accountId, 'rev-2');
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: shown.planDigest}), {ok: false, reason: 'plan-changed'});
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

test('confirmExternalAction_AnotherHumansAgent_NotFound', () => scenario(async ({h}) => {
    const {agent} = await ownedAgent(h);
    const other = await ownedAgent(h);
    const action = await (await propose(h, agent)).json() as {actionId: string};
    assert.deepEqual(await decide(h, other.humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: 'A'.repeat(43)}), {ok: false, reason: 'not-found'});
    assert.deepEqual(await decide(h, other.humanId, {kind: 'reject-action', requestId: action.actionId}), {ok: false, reason: 'not-found'});
}));

// The approval-time credential re-check: the owner reviewed the plan for one connected account, which no longer stands.
for (const change of ['revoked', 'retargeted'] as const)
    test(`confirmExternalAction_CredentialChangedWhilePending_${change}_PlanChangedAndStillPending`, () => scenario(async ({h, provider}) => {
        const {agent, humanId} = await ownedAgent(h);
        const action = await (await propose(h, agent)).json() as {actionId: string};
        const [shown] = await pendingFor(h, humanId);
        if (change === 'retargeted') await setCredential(h, agent.accountId, 'rev-1', 'urn:li:person:beta', 'Desk Beta');
        else await h.db.prepare('UPDATE external_credentials SET revoked_at=CAST(? AS INTEGER) WHERE account_id=?').bind(ticks(), agent.accountId).run();

        assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: action.actionId, planDigest: shown.planDigest}), {ok: false, reason: 'plan-changed'});
        assert.equal((await execute(h, agent, action.actionId)).status, 'pending');
        assert.equal(provider.calls.length, 0);
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

test('decideExternalAction_AlreadyDecided_NotPendingAndOutcomeKept', () => scenario(async ({h, provider}) => {
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

test('getExternalAction_AnotherAgent_Unavailable', () => scenario(async ({h, provider}) => {
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
