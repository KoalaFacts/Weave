import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { connectAccount } from '../src/external-credentials.ts';
import { publishingProvider } from '../src/publishing.ts';
const connectBluesky = (env: Env, {handle, appPassword, ...owner}: {humanId: string; authAt: number; agentId: string; handle: string; appPassword: string}) =>
    connectAccount(env, {...owner, provider: 'bluesky', fields: {handle, appPassword}});
import { openSecret } from '../src/credential-vault.ts';
import { bluesky, facets } from '../src/bluesky-provider.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const key = randomBytes(32).toString('base64url');
const xrpc = 'https://bsky.social/xrpc/';
const did = 'did:plc:' + 'a'.repeat(24), handle = 'hscbeats.bsky.social', appPassword = 'abcd-efgh-ijkl-mnop';
const environment = (h: Harness) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
    REGISTRATION_GLOBAL_PER_HOUR: '100', EXTERNAL_CREDENTIAL_KEY: key}) as unknown as Env;
const session = (overrides: Record<string, unknown> = {}) => Response.json({did, handle, accessJwt: 'jwt-' + randomBytes(4).toString('hex'), refreshJwt: 'r', active: true, ...overrides});

type Fake = {calls: {method: string; auth: string | null; type: string | null; body: Buffer}[]; session: () => Response; upload: (bytes: Buffer) => Response; create: () => Response};
async function scenario(run: (s: {h: Harness; b: Fake; env: Env}) => Promise<void>) {
    const b: Fake = {calls: [], session: () => session(),
        upload: bytes => Response.json({blob: {$type: 'blob', ref: {$link: 'bafk' + createHash('sha256').update(bytes).digest('hex').slice(0, 20)}, mimeType: 'image/png', size: bytes.length}}),
        create: () => Response.json({uri: `at://${did}/app.bsky.feed.post/3kabcde${b.calls.length}`, cid: 'bafyrei'})};
    const h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key}, outboundService: async (request: Request) => {
        const method = request.url.startsWith(xrpc) ? request.url.slice(xrpc.length) : request.url;
        b.calls.push({method, auth: request.headers.get('authorization'), type: request.headers.get('content-type'), body: Buffer.from(await request.arrayBuffer())});
        if (method === 'com.atproto.server.createSession') {
            const {identifier, password} = JSON.parse(b.calls.at(-1)!.body.toString());
            return identifier === did && password === appPassword ? b.session() : Response.json({error: 'AuthenticationRequired'}, {status: 401});
        }
        if (!request.headers.get('authorization')?.startsWith('Bearer jwt-')) return Response.json({error: 'AuthMissing'}, {status: 401});
        if (method === 'com.atproto.repo.uploadBlob') return b.upload(b.calls.at(-1)!.body);
        if (method === 'com.atproto.repo.createRecord') return b.create();
        return new Response('refused', {status: 599});
    }});
    try { await run({h, b, env: environment(h)}); } finally { await h.close(); }
}
/** Replaces fetch for Bluesky session creation while `run` executes (connecting runs in this process). */
async function withBluesky(respond: (body: {identifier: string; password: string}) => Response, run: (calls: number[]) => Promise<void>) {
    const original = globalThis.fetch, calls: number[] = [];
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        assert.equal(String(input), xrpc + 'com.atproto.server.createSession');
        assert.equal(init?.redirect, 'manual');
        calls.push(1);
        return respond(JSON.parse(String(init?.body)));
    };
    try { await run(calls); } finally { globalThis.fetch = original; }
}
const verifying = (body: {identifier: string; password: string}) => body.identifier === handle && body.password === appPassword ? session() : Response.json({error: 'AuthenticationRequired'}, {status: 401});
async function connected(h: Harness, env: Env) {
    const agent = await h.register(), humanId = await h.claim(agent);
    await withBluesky(verifying, async () => {
        assert.deepEqual(await connectBluesky(env, {humanId, authAt: Date.now(), agentId: agent.accountId, handle: '@HSCBeats.bsky.social', appPassword}), {ok: true, accounts: ['@' + handle]});
    });
    return {agent, humanId};
}
const ref = 'bluesky:' + did;
const propose = (h: Harness, agent: any, content: unknown) => h.request('/v1/external-actions', agent, 'POST', {credentialRef: ref, operation: 'publish_post', content: JSON.stringify(content)});
async function approve(env: Env, humanId: string, actionId: string) {
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    const shown = overview.externalActions.find(a => a.actionId === actionId)!;
    assert.deepEqual(await ownerAction(env, {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: actionId, planDigest: shown.planDigest} as OwnerAction), {ok: true});
}
async function approvedPost(h: Harness, env: Env, content: unknown | ((agent: any) => Promise<unknown>)) {
    const {agent, humanId} = await connected(h, env);
    const response = await propose(h, agent, typeof content === 'function' ? await (content as (a: any) => Promise<unknown>)(agent) : content);
    assert.equal(response.status, 200, await response.clone().text());
    const {actionId} = await response.json() as {actionId: string};
    await approve(env, humanId, actionId);
    return {agent, actionId};
}
const execute = (h: Harness, agent: any, actionId: string) => h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {});
const upload = async (h: Harness, agent: any, bytes: Buffer, type: string) => (await h.fetchAs(agent, 'http://localhost/v1/external-media',
    {method: 'PUT', headers: {'Content-Type': type, 'Content-Length': String(bytes.length)}, body: bytes})).json() as Promise<{mediaId: string; bytes: number; sha256: string}>;
const methods = (b: Fake) => b.calls.map(call => call.method);

test('connectBluesky_AppPassword_StoresVerifiedAccountAndSealedPassword', () => scenario(async ({h, env}) => {
    const {agent, humanId} = await connected(h, env);
    const row = (await h.db.prepare(`SELECT credential_ref,provider,revision,target_account,target_display,secret FROM external_credentials WHERE account_id=?`)
        .bind(agent.accountId).first<any>())!;
    assert.deepEqual([row.credential_ref, row.provider, row.target_account, row.target_display], [ref, 'bluesky', did, '@' + handle]);
    const sealed = row.secret instanceof ArrayBuffer ? new Uint8Array(row.secret) : Uint8Array.from(row.secret);
    assert.ok(!Buffer.from(sealed).toString('latin1').includes(appPassword), 'the app password is never stored in clear');
    assert.equal(await openSecret(key, {accountId: agent.accountId, credentialRef: ref, provider: 'bluesky', revision: row.revision}, sealed), appPassword);
    const listed = await h.ok('/v1/external-credentials', agent);
    assert.deepEqual(listed.items.map((item: any) => [item.credentialRef, item.operations]), [[ref, ['publish_post']]]);
    // Reconnecting the same account rotates its revision.
    await withBluesky(verifying, async () => { await connectBluesky(env, {humanId, authAt: Date.now(), agentId: agent.accountId, handle, appPassword}); });
    assert.notEqual((await h.db.prepare('SELECT revision FROM external_credentials WHERE account_id=?').bind(agent.accountId).first<string>('revision')), row.revision);
}));

test('connectBluesky_Refusals_NothingStored', () => scenario(async ({h, env}) => {
    const agent = await h.register(), humanId = await h.claim(agent), other = await h.claim(await h.register());
    const input = (overrides: Record<string, unknown> = {}) => ({humanId, authAt: Date.now(), agentId: agent.accountId, handle, appPassword, ...overrides});
    await withBluesky(verifying, async calls => {
        for (const [name, value] of [['account password', input({appPassword: 'hunter2-my-real-password'})], ['bad handle', input({handle: 'not a handle'})]] as const)
            assert.deepEqual(await connectBluesky(env, value as any), {ok: false, reason: 'provider-refused'}, name);
        for (const fields of [{handle, appPassword, extra: '1'}, {handle}, {handle, appPassword: 'x\ny'}])
            assert.deepEqual(await connectAccount(env, {humanId, authAt: Date.now(), agentId: agent.accountId, provider: 'bluesky', fields: fields as any}), {ok: false, reason: 'not-found'},
                'exactly the platform\'s fields, as printable text: ' + Object.keys(fields));
        assert.equal(calls.length, 0, 'a value that is not an app password is never sent to Bluesky');
        assert.deepEqual(await connectBluesky(env, input({authAt: Date.now() - 11 * 60_000})), {ok: false, reason: 'stale-authentication'});
        assert.deepEqual(await connectBluesky(env, input({humanId: other})), {ok: false, reason: 'not-found'});
        assert.deepEqual(await connectBluesky(env, input({appPassword: 'zzzz-zzzz-zzzz-zzzz'})), {ok: false, reason: 'provider-refused'});
    });
    await withBluesky(() => session({active: false}), async () => assert.deepEqual(await connectBluesky(env, input()), {ok: false, reason: 'provider-refused'}, 'a deactivated account'));
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_credentials').first<number>('n'), 0);
}));

test('blueskyRules_Limits_TextGraphemesBytesImagesAndNoSchedule', async () => {
    // Text-only and too-many-media posts are refused before any stored file is read, so no transaction is needed here.
    const check = (post: unknown) => publishingProvider(bluesky).checkContent!(undefined as never, {accountId: '', operation: 'publish_post', content: JSON.stringify(post), targetAccount: 'did:plc:x'});
    const image = {mediaId: crypto.randomUUID(), bytes: 1000, sha256: 'a'.repeat(64)};
    assert.equal(await check({text: '👍🏽'.repeat(300), media: []}), null, '300 graphemes even when each is several code points');
    assert.match((await check({text: 'x'.repeat(301), media: []}))!, /at most 300 graphemes/);
    assert.match((await check({text: '👨‍👩‍👧‍👦'.repeat(121), media: []}))!, /3000 bytes/, '121 graphemes of 25 bytes each');
    assert.match((await check({text: 'x', media: [image, image, image, image, image]}))!, /at most 4 media/);
    assert.match((await check({text: 'x', media: [], publishAt: new Date(Date.now() + 3600_000).toISOString().replace(/\.\d{3}Z$/, 'Z')}))!, /publishAt is not supported/);
    assert.match((await check({text: 'x', media: [], settings: {__type: 'bluesky'}}))!, /settings are not taken/);
});

test('facets_LinksAndTags_MarkedAtUtf8ByteOffsets', () => {
    const text = '新歌 🎧 https://hscbeats.example/drop. #HSC #2026 #beats!';
    const marked = facets(text), bytes = new TextEncoder().encode(text), slice = (f: any) => new TextDecoder().decode(bytes.slice(f.index.byteStart, f.index.byteEnd));
    assert.deepEqual(marked.map(slice), ['https://hscbeats.example/drop', '#HSC', '#beats']);
    assert.deepEqual(marked.map(f => f.features[0]), [{$type: 'app.bsky.richtext.facet#link', uri: 'https://hscbeats.example/drop'},
        {$type: 'app.bsky.richtext.facet#tag', tag: 'HSC'}, {$type: 'app.bsky.richtext.facet#tag', tag: 'beats'}]);
});

test('facets_LinkEndingInItsOwnBracket_KeepsTheBracket', () => {
    const uri = (text: string) => facets(text).map(f => f.features[0].uri);
    assert.deepEqual(uri('See https://en.wikipedia.org/wiki/Function_(mathematics).'), ['https://en.wikipedia.org/wiki/Function_(mathematics)']);
    assert.deepEqual(uri('(see https://example.com/a)'), ['https://example.com/a'], 'a bracket the link did not open is the sentence\'s');
    assert.deepEqual(uri('[https://example.com/x_(y)]!'), ['https://example.com/x_(y)']);
});

test('executeBlueskyPost_Text_PostsOnceAsTheVerifiedAccount', () => scenario(async ({h, b, env}) => {
    const {agent, actionId} = await approvedPost(h, env, {text: 'New beat out now https://hscbeats.example #HSC', media: []});
    assert.deepEqual(b.calls, [], 'approval alone sends nothing');
    const first = await execute(h, agent, actionId), again = await execute(h, agent, actionId);
    assert.equal(first.status, 'completed');
    assert.match(first.receipt, new RegExp(`^at://${did}/app\\.bsky\\.feed\\.post/`));
    assert.deepEqual(again.receipt, first.receipt);
    assert.deepEqual(methods(b), ['com.atproto.server.createSession', 'com.atproto.repo.createRecord'], 'posted exactly once');
    const {repo, collection, record} = JSON.parse(b.calls[1].body.toString());
    assert.deepEqual([repo, collection, record.$type, record.text], [did, 'app.bsky.feed.post', 'app.bsky.feed.post', 'New beat out now https://hscbeats.example #HSC']);
    assert.deepEqual(record.facets.map((f: any) => f.features[0].$type), ['app.bsky.richtext.facet#link', 'app.bsky.richtext.facet#tag']);
    assert.ok(!b.calls[1].body.toString().includes(appPassword), 'the app password only opens the session');
}));

test('executeBlueskyPost_Images_UploadsExactBytesWithAltText', () => scenario(async ({h, b, env}) => {
    const png = randomBytes(5000), jpeg = randomBytes(3000);
    const {agent, actionId} = await approvedPost(h, env, async (agent: any) => {
        const [a, c] = [await upload(h, agent, png, 'image/png'), await upload(h, agent, jpeg, 'image/jpeg')];
        return {text: '', media: [{mediaId: a.mediaId, bytes: a.bytes, sha256: a.sha256, alt: 'Album cover'}, {mediaId: c.mediaId, bytes: c.bytes, sha256: c.sha256, alt: 'Studio'}]};
    });
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    assert.deepEqual(methods(b), ['com.atproto.server.createSession', 'com.atproto.repo.uploadBlob', 'com.atproto.repo.uploadBlob', 'com.atproto.repo.createRecord']);
    assert.ok(b.calls[1].body.equals(png) && b.calls[1].type === 'image/png');
    assert.ok(b.calls[2].body.equals(jpeg) && b.calls[2].type === 'image/jpeg');
    const embed = JSON.parse(b.calls[3].body.toString()).record.embed;
    assert.equal(embed.$type, 'app.bsky.embed.images');
    assert.deepEqual(embed.images.map((i: any) => i.alt), ['Album cover', 'Studio']);
}));

test('proposeBlueskyPost_UnpinnedOrUnsupportedMedia_Refused', () => scenario(async ({h, env}) => {
    const {agent} = await connected(h, env);
    const gif = await upload(h, agent, randomBytes(100), 'image/gif');
    for (const media of [[{mediaId: gif.mediaId, bytes: gif.bytes, sha256: gif.sha256, alt: 'x'}], [{mediaId: gif.mediaId, bytes: gif.bytes + 1, sha256: gif.sha256, alt: 'x'}]])
        assert.equal((await propose(h, agent, {text: 'x', media})).status, 400);
}));

test('executeBlueskyPost_RefusedBeforeOrAtCreate_FailedWithNothingPosted', () => scenario(async ({h, b, env}) => {
    const cases: [string, (b: Fake) => void, string][] = [
        ['session refused', b => { b.session = () => Response.json({error: 'AuthenticationRequired'}, {status: 401}); }, 'provider-rejected: credential-refused'],
        ['another account', b => { b.session = () => session({did: 'did:plc:' + 'b'.repeat(24)}); }, 'provider-rejected: credential-refused'],
        ['create refused', b => { b.create = () => Response.json({error: 'InvalidRequest'}, {status: 400}); }, 'provider-rejected: bluesky-refused-400'],
        ['rate limited', b => { b.create = () => Response.json({error: 'RateLimitExceeded'}, {status: 429}); }, 'provider-rejected: bluesky-refused-429'],
    ];
    for (const [name, arrange, failure] of cases) {
        const fresh = {session: b.session, create: b.create};
        arrange(b);
        const {agent, actionId} = await approvedPost(h, env, {text: name, media: []});
        const result = await execute(h, agent, actionId);
        assert.deepEqual([result.status, result.failure], ['failed', failure], name);
        Object.assign(b, fresh);
    }
}));

test('executeBlueskyPost_MediaUploadRefused_FailedWithNoPost', () => scenario(async ({h, b, env}) => {
    b.upload = () => Response.json({error: 'BlobTooLarge'}, {status: 400});
    const {agent, actionId} = await approvedPost(h, env, async (agent: any) => {
        const a = await upload(h, agent, randomBytes(500), 'image/webp');
        return {text: 'x', media: [{mediaId: a.mediaId, bytes: a.bytes, sha256: a.sha256, alt: ''}]};
    });
    const result = await execute(h, agent, actionId);
    assert.deepEqual([result.status, result.failure], ['failed', 'provider-rejected: media-upload-refused']);
    assert.ok(!methods(b).includes('com.atproto.repo.createRecord'));
}));

test('executeBlueskyPost_ServerErrorAtCreate_UnknownAndNeverResent', () => scenario(async ({h, b, env}) => {
    b.create = () => new Response('upstream', {status: 502});
    const {agent, actionId} = await approvedPost(h, env, {text: 'x', media: []});
    assert.equal((await execute(h, agent, actionId)).status, 'unknown');
    assert.equal((await execute(h, agent, actionId)).status, 'unknown');
    assert.equal(methods(b).filter(m => m === 'com.atproto.repo.createRecord').length, 1);
}));
