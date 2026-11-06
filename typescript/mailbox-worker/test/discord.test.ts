import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { connectAccount } from '../src/external-credentials.ts';
import { openSecret } from '../src/credential-vault.ts';
import { discord } from '../src/discord-provider.ts';
import { publishingProvider } from '../src/publishing.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const key = randomBytes(32).toString('base64url');
const hookId = '112233445566778899', channelId = '998877665544332211', token = 'T'.repeat(68);
const webhookUrl = `https://discord.com/api/webhooks/${hookId}/${token}`;
const environment = (h: Harness) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
    REGISTRATION_GLOBAL_PER_HOUR: '100', EXTERNAL_CREDENTIAL_KEY: key}) as unknown as Env;
const connectDiscord = (env: Env, {webhookUrl, ...owner}: {humanId: string; authAt: number; agentId: string; webhookUrl: string}) =>
    connectAccount(env, {...owner, provider: 'discord', fields: {webhookUrl}});

type Call = {method: string; url: URL; payload?: any; files?: Map<string, {type: string; name: string; bytes: Buffer}>};
/** A Discord webhook API fake; `answer` overrides the reply to a post. Only the configured webhook exists. */
type Fake = {calls: Call[]; webhook: () => Response; answer?: (call: Call) => Response};
function discordApi(fake: Fake) {
    return async (request: Request): Promise<Response> => {
        const url = new URL(request.url), call: Call = {method: request.method, url};
        fake.calls.push(call);
        if (url.origin !== 'https://discord.com' || url.pathname !== `/api/v10/webhooks/${hookId}/${token}`) return Response.json({message: 'Unknown Webhook'}, {status: 404});
        if (request.method === 'GET') return fake.webhook();
        const form = await request.formData();
        call.payload = JSON.parse(String(form.get('payload_json')));
        call.files = new Map();
        for (const [name, value] of form) if (typeof value !== 'string') call.files.set(name, {type: value.type, name: value.name, bytes: Buffer.from(await value.arrayBuffer())});
        if (fake.answer) return fake.answer(call);
        return Response.json({id: '123456789012345678', channel_id: channelId, content: call.payload.content});
    };
}
const hook = (overrides: Record<string, unknown> = {}) => () => Response.json({id: hookId, type: 1, channel_id: channelId, guild_id: '1'.repeat(18), name: 'HSC Beats', token, ...overrides});
async function scenario(run: (s: {h: Harness; fake: Fake; env: Env}) => Promise<void>) {
    const fake: Fake = {calls: [], webhook: hook()};
    const h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key}, outboundService: discordApi(fake)});
    try { await run({h, fake, env: environment(h)}); } finally { await h.close(); }
}
/** Runs connecting in this process against the same fake. */
async function withDiscord(fake: Fake, run: () => Promise<void>) {
    const original = globalThis.fetch, handler = discordApi(fake);
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        assert.equal(init?.redirect, 'manual');
        return handler(new Request(String(input), init));
    };
    try { await run(); } finally { globalThis.fetch = original; }
}
const ref = 'discord:' + hookId;
async function connected(h: Harness, env: Env, fake: Fake) {
    const agent = await h.register(), humanId = await h.claim(agent);
    await withDiscord(fake, async () => {
        assert.deepEqual(await connectDiscord(env, {humanId, authAt: Date.now(), agentId: agent.accountId, webhookUrl}), {ok: true, accounts: [`HSC Beats (channel ${channelId})`]});
    });
    fake.calls.length = 0;
    return {agent, humanId};
}
const propose = (h: Harness, agent: any, content: unknown) => h.request('/v1/external-actions', agent, 'POST', {credentialRef: ref, operation: 'publish_post', content: JSON.stringify(content)});
async function approve(env: Env, humanId: string, actionId: string) {
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    assert.deepEqual(await ownerAction(env, {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: actionId,
        planDigest: overview.externalActions.find(a => a.actionId === actionId)!.planDigest} as OwnerAction), {ok: true});
}
async function approvedPost(h: Harness, env: Env, fake: Fake, content: unknown | ((agent: any) => Promise<unknown>)) {
    const {agent, humanId} = await connected(h, env, fake);
    const response = await propose(h, agent, typeof content === 'function' ? await (content as (a: any) => Promise<unknown>)(agent) : content);
    assert.equal(response.status, 200, await response.clone().text());
    const {actionId} = await response.json() as {actionId: string};
    await approve(env, humanId, actionId);
    return {agent, actionId};
}
const execute = (h: Harness, agent: any, actionId: string) => h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {});
const upload = async (h: Harness, agent: any, bytes: Buffer, type: string) => {
    const media = await (await h.fetchAs(agent, 'http://localhost/v1/external-media', {method: 'PUT', headers: {'Content-Type': type, 'Content-Length': String(bytes.length)}, body: bytes}))
        .json() as {mediaId: string; bytes: number; sha256: string};
    return {mediaId: media.mediaId, bytes: media.bytes, sha256: media.sha256};
};
const posts = (fake: Fake) => fake.calls.filter(call => call.method === 'POST');

test('connectDiscord_IncomingWebhook_StoresChannelAndSealedUrl', () => scenario(async ({h, fake, env}) => {
    const {agent} = await connected(h, env, fake);
    const row = (await h.db.prepare('SELECT credential_ref,provider,revision,target_account,target_display,secret FROM external_credentials WHERE account_id=?').bind(agent.accountId).first<any>())!;
    assert.deepEqual([row.credential_ref, row.provider, row.target_account, row.target_display], [ref, 'discord', channelId, `HSC Beats (channel ${channelId})`]);
    const sealed = row.secret instanceof ArrayBuffer ? new Uint8Array(row.secret) : Uint8Array.from(row.secret);
    assert.ok(!Buffer.from(sealed).toString('latin1').includes(token), 'the webhook token is never stored in clear');
    assert.equal(await openSecret(key, {accountId: agent.accountId, credentialRef: ref, provider: 'discord', revision: row.revision}, sealed), webhookUrl);
    assert.deepEqual((await h.ok('/v1/external-credentials', agent)).items.map((item: any) => [item.credentialRef, item.operations, item.textRules]),
        [[ref, ['publish_post'], {max: 2000, unit: 'characters', maxWithMedia: null, maxBytes: null}]]);
}));

test('connectDiscord_NotAWebhookOrRefused_NothingStored', () => scenario(async ({h, fake, env}) => {
    const agent = await h.register(), humanId = await h.claim(agent);
    const attempt = (overrides: Record<string, unknown> = {}) => connectDiscord(env, {humanId, authAt: Date.now(), agentId: agent.accountId, webhookUrl, ...overrides} as any);
    await withDiscord(fake, async () => {
        for (const [name, url] of [['another host', `https://evil.example/api/webhooks/${hookId}/${token}`], ['plain http', webhookUrl.replace('https', 'http')],
            ['no token', `https://discord.com/api/webhooks/${hookId}`], ['a password', 'hunter2-my-password']] as const)
            assert.deepEqual(await attempt({webhookUrl: url}), {ok: false, reason: 'provider-refused'}, name);
        assert.equal(fake.calls.length, 0, 'a value that is not a Discord webhook URL is never sent anywhere');
        for (const [name, answer] of [['unknown webhook', () => Response.json({message: 'Unknown Webhook'}, {status: 404})],
            ['a channel-follower webhook', hook({type: 2})], ['another webhook id', hook({id: '1'.repeat(18)})]] as const) {
            fake.webhook = answer;
            assert.deepEqual(await attempt(), {ok: false, reason: 'provider-refused'}, name);
        }
        fake.webhook = hook();
        assert.deepEqual(await attempt({authAt: Date.now() - 11 * 60_000}), {ok: false, reason: 'stale-authentication'});
        // The legacy discordapp.com host is accepted and its requests go to discord.com.
        assert.equal((await attempt({webhookUrl: webhookUrl.replace('discord.com', 'discordapp.com')})).ok, true);
    });
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_credentials').first<number>('n'), 1);
}));

test('discordRules_Limits_TextFilesSizeAndNoSchedule', async () => {
    // Every refusal here comes before any stored file is read, so no transaction is needed.
    const check = (post: unknown) => publishingProvider(discord).checkContent!(undefined as never, {accountId: '', operation: 'publish_post', content: JSON.stringify(post), targetAccount: channelId});
    const file = (bytes = 1000) => ({mediaId: crypto.randomUUID(), bytes, sha256: 'a'.repeat(64)});
    assert.equal(await check({text: 'x'.repeat(2000), media: []}), null);
    const cases: [unknown, RegExp][] = [[{text: 'x'.repeat(2001), media: []}, /at most 2000 characters/], [{text: '', media: []}, /text or media is required/],
        [{text: 'x', media: Array.from({length: 11}, () => file())}, /at most 10 media/], [{text: 'x', media: [file(6_000_000), file(6_000_000)]}, /10485760 bytes in all/],
        [{text: 'x', media: [], publishAt: new Date(Date.now() + 3600_000).toISOString().replace(/\.\d{3}Z$/, 'Z')}, /publishAt is not supported/],
        [{text: 'x', media: [], settings: {tts: true}}, /settings are not taken/],
        [{text: 'x', media: [{...file(), alt: 'a'.repeat(1024) + 'Z'}]}, /alt must be at most 1024 characters/]];
    for (const [post, reason] of cases) assert.match((await check(post)) ?? '', reason, JSON.stringify(post).slice(0, 70));
    assert.ok(publishingProvider(discord).describeContent(channelId).includes('at most 1024 characters'), 'agents are told the alt limit');
});

test('executeDiscordPost_Text_PostedOnceWithNoPings', () => scenario(async ({h, fake, env}) => {
    const {agent, actionId} = await approvedPost(h, env, fake, {text: 'New drop tonight @everyone <@&123>', media: []});
    assert.deepEqual(posts(fake), [], 'approval alone sends nothing');
    const first = await execute(h, agent, actionId), again = await execute(h, agent, actionId);
    assert.deepEqual([first.status, first.receipt, again.receipt], ['completed', channelId + '/123456789012345678', channelId + '/123456789012345678']);
    assert.equal(posts(fake).length, 1, 'posted exactly once');
    const sent = posts(fake)[0];
    assert.equal(sent.url.searchParams.get('wait'), 'true', 'the reply carries the created message');
    assert.deepEqual(sent.payload, {content: 'New drop tonight @everyone <@&123>', allowed_mentions: {parse: []}}, 'mentions stay text');
}));

test('executeDiscordPost_Files_UploadExactBytesWithAltText', () => scenario(async ({h, fake, env}) => {
    const png = randomBytes(4000), mp4 = randomBytes(6000);
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) => {
        const [a, b] = [await upload(h, agent, png, 'image/png'), await upload(h, agent, mp4, 'video/mp4')];
        return {text: 'Cover and teaser', media: [{...a, alt: 'A'.repeat(1023) + 'Z'}, b]};
    });
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    const sent = posts(fake)[0];
    assert.deepEqual(sent.payload.attachments, [{id: 0, filename: 'media0.png', description: 'A'.repeat(1023) + 'Z'}, {id: 1, filename: 'media1.mp4'}],
        'a 1024-character description is sent whole');
    assert.ok(sent.files!.get('files[0]')!.bytes.equals(png) && sent.files!.get('files[0]')!.type === 'image/png');
    assert.ok(sent.files!.get('files[1]')!.bytes.equals(mp4) && sent.files!.get('files[1]')!.type === 'video/mp4');
}));

test('proposeDiscordPost_UnsupportedOrUnpinnedMedia_Refused', () => scenario(async ({h, fake, env}) => {
    const {agent} = await connected(h, env, fake);
    const pdf = await upload(h, agent, randomBytes(100), 'application/pdf'), png = await upload(h, agent, randomBytes(100), 'image/png');
    for (const media of [[pdf], [{...png, bytes: png.bytes + 1}]]) assert.equal((await propose(h, agent, {text: 'x', media})).status, 400);
}));

test('executeDiscordPost_RefusedOrUncertain_FailedOrUnknownAndNeverResent', () => scenario(async ({h, fake, env}) => {
    for (const [status, expected] of [[400, 'failed'], [404, 'failed'], [429, 'failed'], [502, 'unknown']] as const) {
        fake.answer = () => status === 502 ? new Response('bad gateway', {status}) : Response.json({message: 'refused'}, {status});
        const {agent, actionId} = await approvedPost(h, env, fake, {text: 'status ' + status, media: []});
        const result = await execute(h, agent, actionId);
        assert.equal(result.status, expected, String(status));
        if (expected === 'failed') assert.equal(result.failure, 'provider-rejected: discord-refused-' + status);
        fake.answer = undefined;
        await execute(h, agent, actionId);
        assert.equal(posts(fake).length, 1, 'never sent again: ' + status);
        fake.calls.length = 0;
    }
    // A reply for another channel is not a confirmation that this post was published.
    fake.answer = () => Response.json({id: '123456789012345678', channel_id: '1'.repeat(18)});
    const {agent, actionId} = await approvedPost(h, env, fake, {text: 'elsewhere', media: []});
    assert.equal((await execute(h, agent, actionId)).status, 'unknown');
}));

test('executeDiscordPost_WebhookMovedOrDeletedAfterApproval_FailedWithNothingPosted', () => scenario(async ({h, fake, env}) => {
    for (const [name, webhook, failure] of [['moved to another channel', hook({channel_id: '1'.repeat(18)}), 'provider-rejected: webhook-moved'],
        ['deleted', () => Response.json({message: 'Unknown Webhook'}, {status: 404}), 'provider-rejected: credential-refused']] as const) {
        fake.webhook = hook();
        const {agent, actionId} = await approvedPost(h, env, fake, {text: name, media: []});
        fake.webhook = webhook;
        const result = await execute(h, agent, actionId);
        assert.deepEqual([result.status, result.failure], ['failed', failure], name);
        assert.equal(posts(fake).length, 0, 'the approved post never reaches another channel: ' + name);
    }
}));
