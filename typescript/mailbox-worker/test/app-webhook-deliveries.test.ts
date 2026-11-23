import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import { stageWebhookDeliveries } from '../src/app-webhooks.ts';
import { atomic, type Env } from '../src/database.ts';

// Push webhook W2 step 2: every external-action status change queues one delivery per live webhook of the action's
// owner, in the transaction that writes the change.

type Harness = Awaited<ReturnType<typeof start>>;
type Call = {actionId: string};
type Provider = {calls: Call[]; respond: (call: Call) => Response};
type Delivery = {event_id: string; webhook_id: string; action_id: string; status: string; occurred_at: string; attempts: number;
    next_attempt_at: string | null; delivered_at: string | null; updated_at: string};

const endpoint = 'https://provider.test/post';
const completed = (call: Call) => Response.json({externalId: 'post-' + call.actionId});

async function scenario(run: (s: {h: Harness; provider: Provider}) => Promise<void>) {
    const provider: Provider = {calls: [], respond: completed};
    const h = await start({bindings: {EXTERNAL_ACTION_TEST_ENDPOINT: endpoint}, outboundService: async (request: Request) => {
        if (request.url !== endpoint) return new Response('unexpected', {status: 599});
        const call = await request.json() as Call;
        provider.calls.push(call);
        return provider.respond(call);
    }});
    try { await run({h, provider}); } finally { await h.close(); }
}

const environment = (h: Harness) => ({db: h.db, EXTERNAL_ACTION_TEST_ENDPOINT: endpoint}) as unknown as Env;
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

async function grant(h: Harness, humanId: string, revoked = false) {
    const grantId = crypto.randomUUID(), at = ticks();
    await h.db.prepare('INSERT INTO owner_grants(grant_id,human_id,client_id,created_at,revoked_at) VALUES(?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))')
        .bind(grantId, humanId, 'https://office.example/client', at, revoked ? at : null).run();
    return grantId;
}

async function webhook(h: Harness, grantId: string, revoked = false) {
    const webhookId = crypto.randomUUID(), at = ticks();
    await h.db.prepare('INSERT INTO app_webhooks(webhook_id,grant_id,url,secret_sealed,created_at,revoked_at) VALUES(?,?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))')
        .bind(webhookId, grantId, 'https://office.example/hooks/' + webhookId, crypto.getRandomValues(new Uint8Array(40)).buffer, at, revoked ? at : null).run();
    return webhookId;
}

const deliveries = async (h: Harness, actionId?: string) => (await h.db.prepare(`SELECT d.event_id,d.webhook_id,d.action_id,d.status,
        CAST(d.occurred_at AS TEXT) occurred_at,d.attempts,CAST(d.next_attempt_at AS TEXT) next_attempt_at,CAST(d.delivered_at AS TEXT) delivered_at,
        CAST(x.updated_at AS TEXT) updated_at
    FROM app_webhook_deliveries d JOIN external_actions x ON x.action_id=d.action_id
    WHERE ?1 IS NULL OR d.action_id=?1 ORDER BY d.occurred_at,d.webhook_id`).bind(actionId ?? null).all<Delivery>()).results;
const statuses = async (h: Harness, actionId: string, webhookId: string) =>
    (await deliveries(h, actionId)).filter(d => d.webhook_id === webhookId).map(d => d.status);

const propose = async (h: Harness, as: AgentCredential) => {
    const response = await h.request('/v1/external-actions', as, 'POST', {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Episode 12 is live.'});
    assert.equal(response.status, 200, await response.clone().text());
    return (await response.json() as {actionId: string}).actionId;
};
const execute = (h: Harness, as: AgentCredential, actionId: string) => h.ok(`/v1/external-actions/${actionId}/execute`, as, 'POST', {});
const decide = (h: Harness, humanId: string, action: Record<string, unknown>) =>
    ownerAction(environment(h), {humanId, authAt: Date.now(), ...action} as OwnerAction);

async function approve(h: Harness, humanId: string, actionId: string) {
    const overview = await ownerOverview(environment(h), humanId) as {externalActions: {actionId: string; planDigest: string}[]};
    const shown = overview.externalActions.find(a => a.actionId === actionId)!;
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: actionId, planDigest: shown.planDigest}), {ok: true});
}

test('statusChange_FullLifecycle_OneDueDeliveryPerChangeWithStableEventIds', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const webhookId = await webhook(h, await grant(h, humanId));

    const actionId = await propose(h, agent);
    const [pending] = await deliveries(h, actionId);
    assert.equal(pending.status, 'pending');
    assert.equal(pending.occurred_at, pending.updated_at, 'occurred at the change itself');
    await approve(h, humanId, actionId);
    const result = await execute(h, agent, actionId);
    assert.equal(result.status, 'completed');

    const rows = await deliveries(h, actionId);
    assert.deepEqual(rows.map(d => d.status), ['pending', 'approved', 'dispatching', 'completed']);
    for (const d of rows) {
        assert.equal(d.webhook_id, webhookId);
        assert.equal(d.event_id, `${webhookId}:${actionId}:${d.status}`);
        assert.equal(d.attempts, 0);
        assert.equal(d.next_attempt_at, d.occurred_at, 'due at once');
        assert.equal(d.delivered_at, null);
    }
    assert.equal(rows.at(-1)!.occurred_at, rows.at(-1)!.updated_at, 'the last delivery is the action\'s latest change');
    assert.equal(new Set(rows.map(d => d.occurred_at)).size, 4, 'each change has its own time');

    // Reading or executing again changes nothing, so nothing more is queued.
    await execute(h, agent, actionId);
    assert.equal((await deliveries(h, actionId)).length, 4);
}));

test('statusChange_OnlyLiveWebhooksOfLiveGrantsOfTheOwner_GetADelivery', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const other = await ownedAgent(h);
    const first = await grant(h, humanId), second = await grant(h, humanId), revokedGrant = await grant(h, humanId, true);
    const a = await webhook(h, first), b = await webhook(h, first), c = await webhook(h, second);
    const revokedHook = await webhook(h, first, true), underRevokedGrant = await webhook(h, revokedGrant);
    const othersHook = await webhook(h, await grant(h, other.humanId));

    const actionId = await propose(h, agent);

    const rows = await deliveries(h, actionId);
    assert.deepEqual(rows.map(d => d.webhook_id).sort(), [a, b, c].sort());
    for (const id of [revokedHook, underRevokedGrant, othersHook]) assert.deepEqual(await statuses(h, actionId, id), []);
    // The other human's own action reaches only their webhook.
    const theirs = await propose(h, other.agent);
    assert.deepEqual((await deliveries(h, theirs)).map(d => d.webhook_id), [othersHook]);
}));

test('statusChange_WebhookOrGrantRevokedMidway_LaterChangesNotQueued', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const grantId = await grant(h, humanId);
    const kept = await webhook(h, grantId), dropped = await webhook(h, grantId);
    const actionId = await propose(h, agent);
    await h.db.prepare('UPDATE app_webhooks SET revoked_at=CAST(? AS INTEGER) WHERE webhook_id=?').bind(ticks(), dropped).run();
    await approve(h, humanId, actionId);
    await h.db.prepare('UPDATE owner_grants SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=?').bind(ticks(), grantId).run();
    assert.equal((await execute(h, agent, actionId)).status, 'completed');

    assert.deepEqual(await statuses(h, actionId, kept), ['pending', 'approved']);
    assert.deepEqual(await statuses(h, actionId, dropped), ['pending']);
}));

test('statusChange_Rejected_QueuesRejected', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const webhookId = await webhook(h, await grant(h, humanId));
    const actionId = await propose(h, agent);
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: actionId}), {ok: true});
    // A second decision is refused before any write, so nothing more is queued.
    assert.deepEqual(await decide(h, humanId, {kind: 'reject-action', requestId: actionId}), {ok: false, reason: 'not-pending'});
    assert.deepEqual(await statuses(h, actionId, webhookId), ['pending', 'rejected']);
}));

for (const [name, respond, status] of [
    ['ProviderRejected', () => Response.json({reason: 'duplicate content'}, {status: 422}), 'failed'],
    ['OutcomeUnknown', () => new Response('{}', {status: 502}), 'unknown'],
] as const)
    test(`statusChange_${name}_QueuesDispatchingThen${status[0].toUpperCase() + status.slice(1)}`, () => scenario(async ({h, provider}) => {
        const {agent, humanId} = await ownedAgent(h);
        const webhookId = await webhook(h, await grant(h, humanId));
        const actionId = await propose(h, agent);
        await approve(h, humanId, actionId);
        provider.respond = respond;
        assert.equal((await execute(h, agent, actionId)).status, status);
        assert.equal((await execute(h, agent, actionId)).status, status);
        assert.equal(provider.calls.length, 1);
        assert.deepEqual(await statuses(h, actionId, webhookId), ['pending', 'approved', 'dispatching', status]);
    }));

test('statusChange_RefusedBeforeDispatch_QueuesFailedWithoutDispatching', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const webhookId = await webhook(h, await grant(h, humanId));
    const actionId = await propose(h, agent);
    await approve(h, humanId, actionId);
    await setCredential(h, agent.accountId, 'rev-2');
    const result = await execute(h, agent, actionId);
    assert.equal(result.failure, 'credential-changed');
    assert.equal(provider.calls.length, 0);
    assert.deepEqual(await statuses(h, actionId, webhookId), ['pending', 'approved', 'failed']);
}));

test('statusChange_OwnerFrozen_NothingQueuedWhileTheActionStillChanges', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const webhookId = await webhook(h, await grant(h, humanId));
    const actionId = await propose(h, agent);
    await approve(h, humanId, actionId);
    await h.db.prepare('UPDATE humans SET frozen_at=CAST(? AS INTEGER) WHERE human_id=?').bind(ticks(), humanId).run();
    const result = await execute(h, agent, actionId);
    assert.equal(result.failure, 'owner-frozen', 'the change itself is unaffected');
    assert.deepEqual(await statuses(h, actionId, webhookId), ['pending', 'approved']);
}));

test('statusChange_ConcurrentExecutes_EachChangeQueuedOnce', () => scenario(async ({h, provider}) => {
    const {agent, humanId} = await ownedAgent(h);
    const webhookId = await webhook(h, await grant(h, humanId));
    const actionId = await propose(h, agent);
    await approve(h, humanId, actionId);
    await Promise.all(Array.from({length: 5}, () => execute(h, agent, actionId)));
    assert.equal(provider.calls.length, 1);
    assert.deepEqual(await statuses(h, actionId, webhookId), ['pending', 'approved', 'dispatching', 'completed']);
}));

test('stageWebhookDeliveries_ChangeThatDidNotHappen_QueuesNothing', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    const actionId = await propose(h, agent);
    const {stamp} = (await h.db.prepare('SELECT CAST(updated_at AS TEXT) stamp FROM external_actions WHERE action_id=?').bind(actionId).first<{stamp: string}>())!;
    // Registered after the proposal, so the pending change was never queued for it.
    const webhookId = await webhook(h, await grant(h, humanId));
    // A guarded update that matched nothing: the action does not have the status, or has it from another change.
    await atomic(h.db, async tx => {
        stageWebhookDeliveries(tx, actionId, 'approved', stamp);
        stageWebhookDeliveries(tx, actionId, 'pending', (BigInt(stamp) + 1n).toString());
    });
    assert.deepEqual(await statuses(h, actionId, webhookId), []);
    // The change that did happen, staged twice, is queued once.
    await atomic(h.db, async tx => {
        stageWebhookDeliveries(tx, actionId, 'pending', stamp);
        stageWebhookDeliveries(tx, actionId, 'pending', stamp);
    });
    assert.deepEqual(await statuses(h, actionId, webhookId), ['pending']);
}));

test('statusChange_NoWebhooks_ActionLifecycleUnchangedAndNothingQueued', () => scenario(async ({h}) => {
    const {agent, humanId} = await ownedAgent(h);
    await grant(h, humanId);
    const actionId = await propose(h, agent);
    await approve(h, humanId, actionId);
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    assert.deepEqual(await deliveries(h), []);
}));
