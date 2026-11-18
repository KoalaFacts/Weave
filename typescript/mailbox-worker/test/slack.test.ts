import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { openSecret } from '../src/credential-vault.ts';
import { connectAccount } from '../src/external-credentials.ts';
import { ownerAction, ownerOverview } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

const key = randomBytes(32).toString('base64url');
const channel = 'C0123456789', fileId = 'F0123456789', target = 'T0123456789:' + channel;
const ref = 'slack:' + target, timestamp = '1800000000.123456';
const secret = 'xoxb-fixture-token';
const approvedImage = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf1sAAAAASUVORK5CYII=', 'base64');
const display = 'Test workspace / #acceptance (' + channel + ')';
const uploadUrl = 'https://files.slack.com/upload/v1/fixture';

type Scenario = Awaited<ReturnType<typeof start>> & {env: Env; agent: AgentCredential; humanId: string};
type Call = {route: string; value: unknown};
type SlackFake = {calls: Call[]; member: boolean; uploadUrl: string; chatReply?: Response; shareReply?: Response};

async function dispatch(fake: SlackFake, request: Request): Promise<Response> {
    const url = new URL(request.url);
    if (url.href === uploadUrl) {
        assert.equal(request.method, 'POST');
        assert.equal(request.headers.get('authorization'), null);
        fake.calls.push({route: 'upload', value: Buffer.from(await request.arrayBuffer())});
        return new Response('OK');
    }
    assert.equal(url.origin, 'https://slack.com', 'request reaches only the Slack API or the scoped upload URL');
    assert.equal(request.headers.get('authorization'), 'Bearer ' + secret);
    const route = url.pathname.slice('/api/'.length);
    if (route === 'conversations.info') {
        assert.equal(request.method, 'GET');
        assert.equal(url.searchParams.get('channel'), channel);
        fake.calls.push({route, value: channel});
        return Response.json({ok: true, channel: {id: channel, name: 'acceptance', is_member: fake.member, is_channel: true}});
    }
    assert.equal(request.method, 'POST');
    const value = request.headers.get('content-type') === 'application/x-www-form-urlencoded'
        ? Object.fromEntries(new URLSearchParams(await request.text())) : await request.json();
    fake.calls.push({route, value});
    switch (route) {
        case 'auth.test': return Response.json({ok: true, team: 'Test workspace', team_id: 'T0123456789', bot_id: 'B0123456789'});
        case 'chat.postMessage': return fake.chatReply ?? Response.json({ok: true, channel, ts: timestamp});
        case 'files.getUploadURLExternal': return Response.json({ok: true, upload_url: fake.uploadUrl, file_id: fileId});
        case 'files.completeUploadExternal': return fake.shareReply ?? Response.json({ok: true, files: [{id: fileId}]});
        default: throw Error('Unexpected test request: ' + route);
    }
}

async function connect(s: Scenario, fake: SlackFake) {
    const original = globalThis.fetch;
    globalThis.fetch = (input, init) => {
        assert.equal(init?.redirect, 'manual');
        return dispatch(fake, new Request(input, init));
    };
    try {
        return await connectAccount(s.env, {humanId: s.humanId, authAt: Date.now(), agentId: s.agent.accountId, provider: 'slack',
            fields: {botToken: secret, channelId: channel}});
    } finally { globalThis.fetch = original; }
}

async function scenario(run: (s: Scenario, fake: SlackFake) => Promise<void>) {
    const fake: SlackFake = {calls: [], member: true, uploadUrl};
    const h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key}, outboundService: (request: Request) => dispatch(fake, request)});
    try {
        const agent = await h.register(), humanId = await h.claim(agent);
        const env = {db: h.db, EXTERNAL_CREDENTIAL_KEY: key} as unknown as Env;
        await run({...h, env, agent, humanId}, fake);
    } finally { await h.close(); }
}

async function approve(s: Scenario, content: object) {
    const action = await s.ok('/v1/external-actions', s.agent, 'POST', {credentialRef: ref, operation: 'publish_post', content: JSON.stringify(content)});
    const beforeApproval = await s.request('/v1/external-actions/' + action.actionId + '/execute', s.agent, 'POST', {});
    assert.equal(beforeApproval.status, 200);
    assert.equal((await beforeApproval.json() as {status: string}).status, 'pending');
    const overview = await ownerOverview(s.env, s.humanId);
    assert.equal(overview.frozen, false);
    const pending = overview.externalActions.find(item => item.actionId === action.actionId);
    assert.ok(pending);
    assert.equal(pending.targetAccount, target);
    assert.deepEqual(await ownerAction(s.env, {humanId: s.humanId, authAt: Date.now(), kind: 'confirm-action',
        requestId: action.actionId, planDigest: pending.planDigest}), {ok: true});
    return action.actionId as string;
}

const execute = (s: Scenario, id: string) => s.ok('/v1/external-actions/' + id + '/execute', s.agent, 'POST', {});
async function image(s: Scenario) {
    const response = await s.fetchAs(s.agent, 'http://localhost/v1/external-media', {method: 'PUT', body: approvedImage,
        headers: {'Content-Type': 'image/png', 'Content-Length': String(approvedImage.length)}});
    assert.equal(response.status, 200);
    const {mediaId, bytes, sha256} = await response.json() as {mediaId: string; bytes: number; sha256: string};
    return {mediaId, bytes, sha256, alt: 'A test image'};
}

test('connectSlack_VerifiedBotAndChannel_SealsOnlyToken', () => scenario(async (s, fake) => {
    fake.member = false;
    assert.deepEqual(await connect(s, fake), {ok: false, reason: 'provider-refused'});
    assert.deepEqual((await s.ok('/v1/external-credentials', s.agent)).items, []);
    fake.member = true;
    assert.deepEqual(await connect(s, fake), {ok: true, accounts: [display]});
    const result = await s.ok('/v1/external-credentials', s.agent);
    assert.equal(result.items[0].credentialRef, ref);
    assert.equal(result.items[0].targetAccount, target);
    assert.equal(result.items[0].mediaRules.max, 1);
    assert.ok(!JSON.stringify(result).includes(secret));
    const stored = await s.db.prepare('SELECT revision,secret FROM external_credentials WHERE account_id=? AND credential_ref=?')
        .bind(s.agent.accountId, ref).first<{revision: string; secret: ArrayBuffer | number[]}>();
    assert.ok(stored);
    const sealed = stored.secret instanceof ArrayBuffer ? new Uint8Array(stored.secret) : Uint8Array.from(stored.secret);
    assert.ok(!Buffer.from(sealed).includes(secret));
    assert.equal(await openSecret(key, {accountId: s.agent.accountId, credentialRef: ref, provider: 'slack', revision: stored.revision}, sealed), secret);
}));

test('executeSlackApprovedText_PostsOnceAndRecordsReceipt', () => scenario(async (s, fake) => {
    assert.deepEqual(await connect(s, fake), {ok: true, accounts: [display]});
    fake.calls.length = 0;
    const id = await approve(s, {text: '<!channel> hello & goodbye', media: []});
    assert.deepEqual(fake.calls, []);
    const sent = await execute(s, id), again = await execute(s, id);
    assert.deepEqual([sent.status, sent.receipt, again.receipt], ['completed', channel + '/' + timestamp, channel + '/' + timestamp]);
    assert.deepEqual(fake.calls, [{route: 'chat.postMessage', value: {channel, text: '&lt;!channel&gt; hello &amp; goodbye', mrkdwn: true,
        parse: 'none', unfurl_links: false, unfurl_media: false}}]);
}));

test('executeSlackApprovedImage_SharesImageAndCaptionTogether', () => scenario(async (s, fake) => {
    assert.deepEqual(await connect(s, fake), {ok: true, accounts: [display]});
    fake.calls.length = 0;
    const uploaded = await image(s);
    const id = await approve(s, {text: 'Caption & text', media: [uploaded]});
    assert.deepEqual(fake.calls, []);
    const sent = await execute(s, id), again = await execute(s, id);
    assert.deepEqual([sent.status, sent.receipt, again.receipt], ['completed', channel + '/' + fileId, channel + '/' + fileId]);
    assert.deepEqual(fake.calls, [{route: 'files.getUploadURLExternal', value: {filename: 'image.png', length: String(approvedImage.length), alt_txt: uploaded.alt}},
        {route: 'upload', value: approvedImage}, {route: 'files.completeUploadExternal', value: {files: JSON.stringify([{id: fileId, title: 'image.png'}]),
            channel_id: channel, initial_comment: 'Caption &amp; text'}}]);
}));

test('executeSlackText_ExplicitRefusalIsRecordedWithoutReplay', () => scenario(async (s, fake) => {
    assert.deepEqual(await connect(s, fake), {ok: true, accounts: [display]});
    fake.calls.length = 0;
    fake.chatReply = Response.json({ok: false, error: 'missing_scope'});
    const id = await approve(s, {text: 'Caption', media: []});
    const sent = await execute(s, id);
    assert.equal(sent.status, 'failed');
    assert.equal(sent.failure, 'provider-rejected: slack-missing_scope');
    assert.equal((await execute(s, id)).status, 'failed');
    assert.deepEqual(fake.calls.map(item => item.route), ['chat.postMessage']);
}));

test('executeSlackImage_UnsafeUploadAddress_NeverSharesOrSendsFallback', () => scenario(async (s, fake) => {
    assert.deepEqual(await connect(s, fake), {ok: true, accounts: [display]});
    fake.calls.length = 0;
    fake.uploadUrl = 'https://example.test/unexpected';
    const id = await approve(s, {text: 'Caption', media: [await image(s)]});
    const sent = await execute(s, id);
    assert.equal(sent.status, 'failed');
    assert.deepEqual(fake.calls.map(item => item.route), ['files.getUploadURLExternal']);
}));

test('executeSlackImage_AmbiguousShareRecordedWithoutReplay', () => scenario(async (s, fake) => {
    assert.deepEqual(await connect(s, fake), {ok: true, accounts: [display]});
    fake.calls.length = 0;
    fake.shareReply = Response.json({ok: false, error: 'internal_error'});
    const id = await approve(s, {text: 'Caption', media: [await image(s)]});
    assert.equal((await execute(s, id)).status, 'unknown');
    assert.equal((await execute(s, id)).status, 'unknown');
    assert.deepEqual(fake.calls.map(item => item.route), ['files.getUploadURLExternal', 'upload', 'files.completeUploadExternal']);
}));
