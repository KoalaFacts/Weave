import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { ownerAction, ownerOverview, reauthWindowMs, type OwnerAction } from '../src/owner-actions.ts';
import { approvalOrigin, proposalKey } from '../src/external-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Call = {operation: string; content: string; targetAccount: string; actionId: string};
type Provider = {calls: Call[]; respond: (call: Call) => Response};

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

for (const change of ['rotated', 'revoked', 'retargeted'] as const)
    test(`executeExternalAction_CredentialChangedAfterApproval_${change}_FailsWithoutSending`, () => scenario(async ({h, provider}) => {
        const {agent, actionId} = await approved(h);
        if (change === 'rotated') await setCredential(h, agent.accountId, 'rev-2');
        else if (change === 'retargeted') await setCredential(h, agent.accountId, 'rev-1', 'urn:li:person:beta', 'Desk Beta');
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

test('executeExternalAction_ApprovalExpired_DoesNotSend', () => scenario(async ({h, provider}) => {
    const {agent, actionId} = await approved(h);
    await h.db.prepare('UPDATE external_actions SET expires_at=CAST(? AS INTEGER) WHERE action_id=?').bind((BigInt(ticks()) - 1n).toString(), actionId).run();
    assert.equal((await execute(h, agent, actionId)).status, 'expired');
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
