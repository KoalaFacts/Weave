import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import { sealWebhookSecret } from '../src/credential-vault.ts';
import { firstRetryMs, maxWebhookAttempts } from '../src/webhook-sender.ts';
import type { Env } from '../src/database.ts';

// Push webhook W3: the scheduled sender POSTs each queued delivery, signed, at least once with bounded retries, never
// following redirects, and never changes the action.

type Harness = Awaited<ReturnType<typeof start>>;
type Received = {url: string; headers: Headers; body: string};
type Receiver = {received: Received[]; respond: (request: Received) => Response | Promise<Response>};
type Delivery = {event_id: string; status: string; attempts: number; next_attempt_at: string | null; delivered_at: string | null};

const endpoint = 'https://provider.test/post';
const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString('base64url');

async function scenario(run: (s: {h: Harness; receiver: Receiver}) => Promise<void>, bindings: Record<string, string> = {EXTERNAL_CREDENTIAL_KEY: key}) {
    const receiver: Receiver = {received: [], respond: () => new Response('ok')};
    const h = await start({bindings: {EXTERNAL_ACTION_TEST_ENDPOINT: endpoint, CLEANUP_MAX_MESSAGES: '0', ...bindings}, outboundService: async (request: Request) => {
        if (request.url === endpoint) return Response.json({externalId: 'post-1'});
        const received = {url: request.url, headers: request.headers, body: await request.text()};
        receiver.received.push(received);
        return receiver.respond(received);
    }});
    try { await run({h, receiver}); } finally { await h.close(); }
}

const ticks = (ms = Date.now()) => (BigInt(ms) * 10000n).toString();
const environment = (h: Harness) => ({db: h.db, EXTERNAL_ACTION_TEST_ENDPOINT: endpoint}) as unknown as Env;
const sweep = async (h: Harness) =>
    assert.equal((await (await h.mf.getWorker()).scheduled({cron: '*/5 * * * *', scheduledTime: Date.now()})).outcome, 'ok');

async function ownedAgent(h: Harness) {
    const agent = await h.register();
    const humanId = await h.claim(agent);
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
        VALUES(?,'linkedin-main','test-http','rev-1','urn:li:person:alpha','Desk Alpha',CAST(? AS INTEGER),CAST(? AS INTEGER))`)
        .bind(agent.accountId, ticks(), ticks()).run();
    return {agent, humanId};
}

async function grant(h: Harness, humanId: string) {
    const grantId = crypto.randomUUID();
    await h.db.prepare('INSERT INTO owner_grants(grant_id,human_id,client_id,created_at) VALUES(?,?,?,CAST(? AS INTEGER))')
        .bind(grantId, humanId, 'https://office.example/client', ticks()).run();
    return grantId;
}

async function webhook(h: Harness, grantId: string, url?: string) {
    const webhookId = crypto.randomUUID(), secret = 'secret-' + webhookId;
    const sealed = await sealWebhookSecret(key, webhookId, grantId, secret);
    await h.db.prepare('INSERT INTO app_webhooks(webhook_id,grant_id,url,secret_sealed,created_at) VALUES(?,?,?,?,CAST(? AS INTEGER))')
        .bind(webhookId, grantId, url ?? 'https://office.example/hooks/' + webhookId, sealed.buffer as ArrayBuffer, ticks()).run();
    return {webhookId, secret};
}

const deliveries = async (h: Harness) => (await h.db.prepare(`SELECT event_id,status,attempts,CAST(next_attempt_at AS TEXT) next_attempt_at,
    CAST(delivered_at AS TEXT) delivered_at FROM app_webhook_deliveries ORDER BY occurred_at,event_id`).all<Delivery>()).results;
const makeDue = (h: Harness) => h.db.prepare('UPDATE app_webhook_deliveries SET next_attempt_at=CAST(? AS INTEGER) WHERE next_attempt_at IS NOT NULL')
    .bind(ticks(Date.now() - 1000)).run();
const actionStatus = async (h: Harness, actionId: string) =>
    await h.db.prepare('SELECT status FROM external_actions WHERE action_id=?').bind(actionId).first<string>('status');

const propose = async (h: Harness, as: AgentCredential) => {
    const response = await h.request('/v1/external-actions', as, 'POST', {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Episode 12 is live.'});
    assert.equal(response.status, 200, await response.clone().text());
    return (await response.json() as {actionId: string}).actionId;
};

async function approve(h: Harness, humanId: string, actionId: string) {
    const overview = await ownerOverview(environment(h), humanId) as {externalActions: {actionId: string; planDigest: string}[]};
    const shown = overview.externalActions.find(a => a.actionId === actionId)!;
    assert.deepEqual(await ownerAction(environment(h), {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: actionId,
        planDigest: shown.planDigest} as OwnerAction), {ok: true});
}

test('sweepWebhooks_DueDelivery_PostsSignedBodyOnceAndMarksDelivered', () => scenario(async ({h, receiver}) => {
    const {agent, humanId} = await ownedAgent(h);
    const {webhookId, secret} = await webhook(h, await grant(h, humanId));
    const actionId = await propose(h, agent);
    const before = Date.now();

    await sweep(h);

    assert.equal(receiver.received.length, 1);
    const [got] = receiver.received;
    const eventId = `${webhookId}:${actionId}:pending`;
    assert.equal(got.url, 'https://office.example/hooks/' + webhookId);
    assert.equal(got.headers.get('content-type'), 'application/json');
    assert.equal(got.headers.get('weave-event-id'), eventId);
    assert.equal(got.headers.get('weave-webhook-id'), webhookId);
    const body = JSON.parse(got.body) as Record<string, unknown>;
    assert.deepEqual(Object.keys(body).sort(), ['actionId', 'eventId', 'occurredAt', 'status', 'type', 'webhookId']);
    assert.deepEqual({...body, occurredAt: undefined}, {eventId, type: 'external_action.status', webhookId, actionId, status: 'pending', occurredAt: undefined});
    assert.match(String(body.occurredAt), /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/);
    assert.ok(!got.body.includes('Episode 12'), 'no content in the body');
    const signature = got.headers.get('weave-signature')!;
    const t = Number(/^t=(\d+),v1=[0-9a-f]{64}$/.exec(signature)![1]);
    assert.ok(Math.abs(t - before / 1000) < 60);
    assert.equal(signature, `t=${t},v1=` + createHmac('sha256', secret).update(`${t}.${got.body}`).digest('hex'),
        'HMAC-SHA256 of "<t>.<body>" under the webhook secret');

    const [row] = await deliveries(h);
    assert.equal(row.attempts, 1);
    assert.equal(row.next_attempt_at, null);
    assert.notEqual(row.delivered_at, null);

    await sweep(h);
    assert.equal(receiver.received.length, 1, 'a delivered event is not sent again');
    // The lifecycle's later changes are delivered by later runs, each with its own event ID.
    await approve(h, humanId, actionId);
    await sweep(h);
    assert.deepEqual(receiver.received.map(r => r.headers.get('weave-event-id')), [eventId, `${webhookId}:${actionId}:approved`]);
}));

test('sweepWebhooks_ReceiverFails_RetriesLaterWithoutChangingTheAction', () => scenario(async ({h, receiver}) => {
    const {agent, humanId} = await ownedAgent(h);
    await webhook(h, await grant(h, humanId));
    const actionId = await propose(h, agent);
    receiver.respond = () => new Response('busy', {status: 503});
    const before = Date.now();

    await sweep(h);

    let [row] = await deliveries(h);
    assert.equal(row.attempts, 1);
    assert.equal(row.delivered_at, null);
    const next = Number(BigInt(row.next_attempt_at!) / 10000n);
    assert.ok(next >= before + firstRetryMs && next <= Date.now() + firstRetryMs, 'the first retry is a minute later');
    assert.equal(await actionStatus(h, actionId), 'pending', 'a delivery failure never changes the action');
    await sweep(h);
    assert.equal(receiver.received.length, 1, 'not retried before it is due');

    receiver.respond = () => new Response(null, {status: 204});
    await makeDue(h);
    await sweep(h);
    [row] = await deliveries(h);
    assert.equal(receiver.received.length, 2);
    assert.equal(receiver.received[0].headers.get('weave-event-id'), receiver.received[1].headers.get('weave-event-id'), 'the same event ID on retry');
    assert.equal(row.attempts, 2);
    assert.notEqual(row.delivered_at, null);
    assert.equal(row.next_attempt_at, null);
}));

test('sweepWebhooks_Redirect_NotFollowedAndCountedAsFailure', () => scenario(async ({h, receiver}) => {
    const {agent, humanId} = await ownedAgent(h);
    await webhook(h, await grant(h, humanId));
    await propose(h, agent);
    receiver.respond = request => request.url.startsWith('https://office.example/')
        ? new Response(null, {status: 307, headers: {Location: 'https://elsewhere.example/steal'}}) : new Response('ok');

    await sweep(h);

    assert.deepEqual(receiver.received.map(r => new URL(r.url).hostname), ['office.example'], 'the redirect target is never called');
    const [row] = await deliveries(h);
    assert.equal(row.delivered_at, null);
    assert.notEqual(row.next_attempt_at, null);
}));

test('sweepWebhooks_LastAttemptFails_GivesUp', () => scenario(async ({h, receiver}) => {
    const {agent, humanId} = await ownedAgent(h);
    await webhook(h, await grant(h, humanId));
    await propose(h, agent);
    receiver.respond = () => new Response('no', {status: 500});
    await h.db.prepare('UPDATE app_webhook_deliveries SET attempts=?').bind(maxWebhookAttempts - 2).run();

    await sweep(h);
    let [row] = await deliveries(h);
    assert.equal(row.attempts, maxWebhookAttempts - 1);
    assert.notEqual(row.next_attempt_at, null, 'one attempt left');
    await makeDue(h);
    await sweep(h);
    [row] = await deliveries(h);
    assert.equal(row.attempts, maxWebhookAttempts);
    assert.equal(row.next_attempt_at, null, 'given up after the last attempt');
    assert.equal(row.delivered_at, null);
    await makeDue(h);
    await sweep(h);
    assert.equal(receiver.received.length, 2, 'never sent again');
}));

test('sweepWebhooks_ReceiverTimesOut_CountedAsFailure', () => scenario(async ({h, receiver}) => {
    const {agent, humanId} = await ownedAgent(h);
    await webhook(h, await grant(h, humanId));
    await propose(h, agent);
    receiver.respond = () => new Promise(resolve => setTimeout(() => resolve(new Response('late')), 8_000));
    const before = Date.now();

    await sweep(h);

    assert.ok(Date.now() - before < 7_500, 'the request is abandoned at the timeout');
    const [row] = await deliveries(h);
    assert.equal(row.attempts, 1);
    assert.equal(row.delivered_at, null);
    assert.notEqual(row.next_attempt_at, null);
}));

for (const [name, end] of [
    ['WebhookRevoked', 'UPDATE app_webhooks SET revoked_at=CAST(?1 AS INTEGER)'],
    ['GrantRevoked', 'UPDATE owner_grants SET revoked_at=CAST(?1 AS INTEGER)'],
    ['HumanFrozen', 'UPDATE humans SET frozen_at=CAST(?1 AS INTEGER)'],
] as const) test(`sweepWebhooks_${name}AfterQueueing_FinishedUnsent`, () => scenario(async ({h, receiver}) => {
    const {agent, humanId} = await ownedAgent(h);
    await webhook(h, await grant(h, humanId));
    await propose(h, agent);
    await h.db.prepare(end).bind(ticks()).run();

    await sweep(h);

    assert.equal(receiver.received.length, 0);
    const [row] = await deliveries(h);
    assert.equal(row.attempts, 0, 'no attempt was made');
    assert.equal(row.next_attempt_at, null, 'finished');
    assert.equal(row.delivered_at, null);
}));

test('sweepWebhooks_AgentNoLongerOwnedByTheGrantsHuman_FinishedUnsent', () => scenario(async ({h, receiver}) => {
    const {agent, humanId} = await ownedAgent(h);
    const other = await ownedAgent(h);
    await webhook(h, await grant(h, humanId));
    await propose(h, agent);
    await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(other.humanId, agent.accountId).run();

    await sweep(h);

    assert.equal(receiver.received.length, 0);
    assert.equal((await deliveries(h))[0].next_attempt_at, null);
}));

test('sweepWebhooks_MoreDueThanOneRun_SendsFifteenWithinTheBudgetThenTheRest', () => scenario(async ({h, receiver}) => {
    const {agent, humanId} = await ownedAgent(h);
    const first = await grant(h, humanId), second = await grant(h, humanId);
    for (let i = 0; i < 8; i++) { await webhook(h, first); await webhook(h, second); }
    await propose(h, agent);
    assert.equal((await deliveries(h)).length, 16);

    await sweep(h);
    assert.equal(receiver.received.length, 15);
    assert.equal((await deliveries(h)).filter(d => d.delivered_at !== null).length, 15);
    await sweep(h);
    assert.equal(receiver.received.length, 16);
    assert.equal(new Set(receiver.received.map(r => r.headers.get('weave-event-id'))).size, 16);
}));

test('sweepWebhooks_NoCredentialKey_SendsNothing', () => scenario(async ({h, receiver}) => {
    const {agent, humanId} = await ownedAgent(h);
    await webhook(h, await grant(h, humanId));
    await propose(h, agent);

    await sweep(h);

    assert.equal(receiver.received.length, 0);
    const [row] = await deliveries(h);
    assert.equal(row.attempts, 0);
    assert.notEqual(row.next_attempt_at, null, 'still due once the key is configured');
}, {}));
