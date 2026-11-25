import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.ts';
import { ownerAction, ownerOverview, reauthWindowMs, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Call = {actionId: string};

const endpoint = 'https://provider.test/post';

async function scenario(run: (s: {h: Harness; respond: (r: () => Response) => void; calls: Call[]}) => Promise<void>) {
    let respond: () => Response = () => new Response('{}', {status: 502});
    const calls: Call[] = [];
    const h = await start({bindings: {EXTERNAL_ACTION_TEST_ENDPOINT: endpoint}, outboundService: async (request: Request) => {
        if (request.url !== endpoint) return new Response('unexpected', {status: 599});
        calls.push(await request.json() as Call);
        return respond();
    }});
    try { await run({h, respond: r => { respond = r; }, calls}); } finally { await h.close(); }
}

const environment = (h: Harness) => ({db: h.db, EXTERNAL_ACTION_TEST_ENDPOINT: endpoint}) as unknown as Env;
const ticks = () => (BigInt(Date.now()) * 10000n).toString();
const decide = (h: Harness, humanId: string, action: Record<string, unknown>, authAt = Date.now()) =>
    ownerAction(environment(h), {humanId, authAt, ...action} as OwnerAction);

/** An owned agent's approved plan, sent once to a provider whose answer is lost, so its outcome is `unknown`. */
async function unknownAction(h: Harness, respond: (r: () => Response) => void) {
    const agent = await h.register();
    const humanId = await h.claim(agent);
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
        VALUES(?,'linkedin-main','test-http','rev-1','urn:li:person:alpha','Desk Alpha',CAST(? AS INTEGER),CAST(? AS INTEGER))`).bind(agent.accountId, ticks(), ticks()).run();
    const proposed = await h.request('/v1/external-actions', agent, 'POST', {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Episode 12 is live.'});
    assert.equal(proposed.status, 200, await proposed.clone().text());
    const {actionId} = await proposed.json() as {actionId: string};
    const overview = await ownerOverview(environment(h), humanId) as {externalActions: {planDigest: string}[]};
    assert.deepEqual(await decide(h, humanId, {kind: 'confirm-action', requestId: actionId, planDigest: overview.externalActions[0].planDigest}), {ok: true});
    respond(() => new Response('{}', {status: 502}));
    const sent = await h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {});
    assert.equal(sent.status, 'unknown');
    return {agent, humanId, actionId};
}

const settlement = (h: Harness, actionId: string) => h.db.prepare(
    'SELECT finding,post_url,settled_by,CAST(settled_at AS TEXT) settled_at FROM external_action_settlements WHERE action_id=?').bind(actionId)
    .first<{finding: string; post_url: string | null; settled_by: string; settled_at: string}>();
const status = (h: Harness, actionId: string) => h.db.prepare('SELECT status FROM external_actions WHERE action_id=?').bind(actionId).first<string>('status');

test('settleAction_OwnerFindsItPosted_RecordedOnceAndNeverResent', () => scenario(async ({h, respond, calls}) => {
    const {agent, humanId, actionId} = await unknownAction(h, respond);
    const url = 'https://www.linkedin.com/feed/update/urn:li:share:1';
    assert.deepEqual(await decide(h, humanId, {kind: 'settle-action', requestId: actionId, finding: 'posted', postUrl: url}), {ok: true});
    const row = await settlement(h, actionId);
    assert.equal(row?.finding, 'posted');
    assert.equal(row?.post_url, url);
    assert.equal(row?.settled_by, humanId);
    assert.ok(BigInt(row!.settled_at) > 0n);
    assert.equal(await status(h, actionId), 'unknown', 'the action keeps its unknown outcome');
    // At most once: a second finding, even a different one, is refused and the first stays.
    assert.deepEqual(await decide(h, humanId, {kind: 'settle-action', requestId: actionId, finding: 'not_posted', postUrl: null}), {ok: false, reason: 'not-pending'});
    assert.equal((await settlement(h, actionId))?.finding, 'posted');
    // Settling never sends it again.
    respond(() => Response.json({externalId: 'post-again'}));
    assert.equal((await h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {})).status, 'unknown');
    assert.equal(calls.length, 1);
}));

test('settleAction_NotPostedOrPostedWithoutLink_Recorded', () => scenario(async ({h, respond}) => {
    const first = await unknownAction(h, respond);
    assert.deepEqual(await decide(h, first.humanId, {kind: 'settle-action', requestId: first.actionId, finding: 'not_posted', postUrl: null}), {ok: true});
    assert.deepEqual({...await settlement(h, first.actionId), settled_at: undefined}, {finding: 'not_posted', post_url: null, settled_by: first.humanId, settled_at: undefined});
    const second = await unknownAction(h, respond);
    assert.deepEqual(await decide(h, second.humanId, {kind: 'settle-action', requestId: second.actionId, finding: 'posted', postUrl: null}), {ok: true});
    assert.equal((await settlement(h, second.actionId))?.post_url, null);
}));

test('settleAction_MalformedFindingOrLink_NotFoundAndNothingWritten', () => scenario(async ({h, respond}) => {
    const {humanId, actionId} = await unknownAction(h, respond);
    for (const [finding, postUrl] of [['posted', 'http://example.test/p'], ['posted', 'https://' + 'a'.repeat(2041)], ['posted', 'https://'], ['posted', 'javascript:alert(1)'],
        ['not_posted', 'https://example.test/p'], ['maybe', null], ['posted', undefined], [undefined, null], ['posted', 42]] as const)
        assert.deepEqual(await decide(h, humanId, {kind: 'settle-action', requestId: actionId, finding, postUrl}), {ok: false, reason: 'not-found'}, `${finding} ${postUrl}`);
    assert.equal(await settlement(h, actionId), null);
    // The longest link the table keeps is accepted.
    const longest = 'https://' + 'a'.repeat(2040);
    assert.equal(longest.length, 2048);
    assert.deepEqual(await decide(h, humanId, {kind: 'settle-action', requestId: actionId, finding: 'posted', postUrl: longest}), {ok: true});
}));

test('settleAction_StaleSignIn_StaleAuthenticationAndNothingWritten', () => scenario(async ({h, respond}) => {
    const {humanId, actionId} = await unknownAction(h, respond);
    assert.deepEqual(await decide(h, humanId, {kind: 'settle-action', requestId: actionId, finding: 'not_posted', postUrl: null}, Date.now() - reauthWindowMs - 60_000),
        {ok: false, reason: 'stale-authentication'});
    assert.equal(await settlement(h, actionId), null);
}));

test('settleAction_AnotherHumansOrFrozenOrNotUnknown_Refused', () => scenario(async ({h, respond}) => {
    const {humanId, actionId} = await unknownAction(h, respond);
    const other = await unknownAction(h, respond);
    assert.deepEqual(await decide(h, other.humanId, {kind: 'settle-action', requestId: actionId, finding: 'not_posted', postUrl: null}), {ok: false, reason: 'not-found'});
    for (const s of ['approved', 'rejected', 'dispatching', 'completed', 'failed']) {
        await h.db.prepare('UPDATE external_actions SET status=? WHERE action_id=?').bind(s, actionId).run();
        assert.deepEqual(await decide(h, humanId, {kind: 'settle-action', requestId: actionId, finding: 'not_posted', postUrl: null}), {ok: false, reason: 'not-pending'}, s);
    }
    await h.db.prepare("UPDATE external_actions SET status='unknown' WHERE action_id=?").bind(actionId).run();
    await h.db.prepare('UPDATE humans SET frozen_at=CAST(? AS INTEGER) WHERE human_id=?').bind(ticks(), humanId).run();
    assert.deepEqual(await decide(h, humanId, {kind: 'settle-action', requestId: actionId, finding: 'not_posted', postUrl: null}), {ok: false, reason: 'frozen'});
    assert.equal(await settlement(h, actionId), null);
}));
