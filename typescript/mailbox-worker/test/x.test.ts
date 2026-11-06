import assert from 'node:assert/strict';
import { createHmac, randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { connectAccount } from '../src/external-credentials.ts';
import { openSecret } from '../src/credential-vault.ts';
import { x, xLength } from '../src/x-provider.ts';
import { publishingProvider } from '../src/publishing.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const key = randomBytes(32).toString('base64url');
const userId = '1450000000000000001';
const keys = {apiKey: 'K'.repeat(25), apiKeySecret: 'S'.repeat(50), accessToken: userId + '-' + 'T'.repeat(40), accessTokenSecret: 'U'.repeat(45)};
const environment = (h: Harness) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
    REGISTRATION_GLOBAL_PER_HOUR: '100', EXTERNAL_CREDENTIAL_KEY: key}) as unknown as Env;
const connectX = (env: Env, {fields = keys, ...owner}: {humanId: string; authAt: number; agentId: string; fields?: Record<string, string>}) =>
    connectAccount(env, {...owner, provider: 'x', fields});

/** RFC 3986 percent-encoding of UTF-8 bytes, written independently of the adapter's. */
const encode = (value: string) => [...new TextEncoder().encode(value)]
    .map(byte => /[A-Za-z0-9\-._~]/.test(String.fromCharCode(byte)) ? String.fromCharCode(byte) : '%' + byte.toString(16).toUpperCase().padStart(2, '0')).join('');
/** An independent OAuth 1.0a HMAC-SHA1 check of one request, against the configured keys. */
function signedByKeys(request: Request, url: URL): boolean {
    const header = request.headers.get('Authorization') ?? '';
    if (!header.startsWith('OAuth ')) return false;
    const params = Object.fromEntries([...header.slice(6).matchAll(/([a-z_]+)="([^"]*)"/g)].map(([, name, value]) => [name, decodeURIComponent(value)]));
    if (params.oauth_consumer_key !== keys.apiKey || params.oauth_token !== keys.accessToken || params.oauth_signature_method !== 'HMAC-SHA1'
        || params.oauth_version !== '1.0' || !/^[0-9a-f]{32}$/.test(params.oauth_nonce) || Math.abs(Number(params.oauth_timestamp) - Date.now() / 1000) > 300) return false;
    const {oauth_signature: signature, ...signed} = params;
    const pairs = [...url.searchParams, ...Object.entries(signed)].map(([n, v]) => [encode(n), encode(v)])
        .sort(([a, x], [b, y]) => a === b ? (x < y ? -1 : x > y ? 1 : 0) : a < b ? -1 : 1).map(([n, v]) => n + '=' + v);
    const base = request.method + '&' + encode(url.origin + url.pathname) + '&' + encode(pairs.join('&'));
    return createHmac('sha1', encode(keys.apiKeySecret) + '&' + encode(keys.accessTokenSecret)).update(base).digest('base64') === signature;
}

type Call = {method: string; path: string; signed: boolean; json?: any; upload?: {category: string; type: string; bytes: Buffer}};
/** An X API v2 fake. `me`, `upload` and `answer` override its replies. */
type Fake = {calls: Call[]; me?: () => Response; upload?: (call: Call) => Response; metadata?: (call: Call) => Response; answer?: (call: Call) => Response};
function xApi(fake: Fake) {
    let media = 1710000000000000000n;
    return async (request: Request): Promise<Response> => {
        const url = new URL(request.url), call: Call = {method: request.method, path: url.pathname, signed: signedByKeys(request, url)};
        fake.calls.push(call);
        if (url.origin !== 'https://api.x.com') return new Response('not found', {status: 404});
        if (!call.signed) return Response.json({title: 'Unauthorized', status: 401}, {status: 401});
        if (request.method === 'GET' && url.pathname === '/2/users/me') return fake.me?.() ?? Response.json({data: {id: userId, name: 'HSC Beats', username: 'hscbeats'}});
        if (request.method === 'POST' && url.pathname === '/2/media/upload') {
            const form = await request.formData(), file = form.get('media') as File;
            call.upload = {category: String(form.get('media_category')), type: file.type, bytes: Buffer.from(await file.arrayBuffer())};
            return fake.upload?.(call) ?? Response.json({data: {id: String(++media), media_key: '3_' + media, size: call.upload.bytes.length}});
        }
        if (request.method === 'POST' && url.pathname === '/2/media/metadata') {
            call.json = await request.json();
            return fake.metadata?.(call) ?? Response.json({data: {id: call.json.id, associated_metadata: {alt_text: call.json.metadata.alt_text}}});
        }
        if (request.method === 'POST' && url.pathname === '/2/tweets') {
            call.json = await request.json();
            return fake.answer?.(call) ?? Response.json({data: {id: '1860000000000000001', text: call.json.text ?? ''}}, {status: 201});
        }
        return new Response('not found', {status: 404});
    };
}
async function scenario(run: (s: {h: Harness; fake: Fake; env: Env}) => Promise<void>) {
    const fake: Fake = {calls: []};
    const h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key}, outboundService: xApi(fake)});
    try { await run({h, fake, env: environment(h)}); } finally { await h.close(); }
}
/** Runs connecting in this process against the same fake. */
async function withX(fake: Fake, run: () => Promise<void>) {
    const original = globalThis.fetch, handler = xApi(fake);
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        assert.equal(init?.redirect, 'manual');
        return handler(new Request(String(input), init));
    };
    try { await run(); } finally { globalThis.fetch = original; }
}
const ref = 'x:' + userId;
async function connected(h: Harness, env: Env, fake: Fake) {
    const agent = await h.register(), humanId = await h.claim(agent);
    await withX(fake, async () => {
        assert.deepEqual(await connectX(env, {humanId, authAt: Date.now(), agentId: agent.accountId}), {ok: true, accounts: ['@hscbeats']});
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
const tweets = (fake: Fake) => fake.calls.filter(call => call.path === '/2/tweets');

test('connectX_SignedKeys_StoresUserAndSealsAllFourValues', () => scenario(async ({h, fake, env}) => {
    const {agent, humanId} = await connected(h, env, fake);
    const row = (await h.db.prepare('SELECT credential_ref,provider,revision,target_account,target_display,secret FROM external_credentials WHERE account_id=?').bind(agent.accountId).first<any>())!;
    assert.deepEqual([row.credential_ref, row.provider, row.target_account, row.target_display], [ref, 'x', userId, '@hscbeats']);
    const sealed = row.secret instanceof ArrayBuffer ? new Uint8Array(row.secret) : Uint8Array.from(row.secret);
    for (const value of Object.values(keys)) assert.ok(!Buffer.from(sealed).toString('latin1').includes(value.slice(-20)), 'no key is stored in clear');
    assert.deepEqual(JSON.parse(await openSecret(key, {accountId: agent.accountId, credentialRef: ref, provider: 'x', revision: row.revision}, sealed)), keys);
    assert.deepEqual((await h.ok('/v1/external-credentials', agent)).items.map((item: any) => [item.credentialRef, item.operations]), [[ref, ['publish_post']]]);
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    const form = overview.connectors.find(connector => connector.provider === 'x')!;
    assert.deepEqual(form.fields.map(field => [field.name, field.secret]), [['apiKey', true], ['apiKeySecret', true], ['accessToken', true], ['accessTokenSecret', true]],
        'the owner page asks for all four values as secrets');
}));

test('connectX_MalformedOrRefusedKeys_NothingStored', () => scenario(async ({h, fake, env}) => {
    const agent = await h.register(), humanId = await h.claim(agent);
    const attempt = (fields: Record<string, string> = keys) => connectX(env, {humanId, authAt: Date.now(), agentId: agent.accountId, fields});
    await withX(fake, async () => {
        for (const [name, fields] of [['a password as the secret', {...keys, apiKeySecret: 'hunter2 my password'}],
            ['an access token without its user', {...keys, accessToken: 'T'.repeat(40)}]] as const)
            assert.deepEqual(await attempt(fields), {ok: false, reason: 'provider-refused'}, name);
        assert.equal(fake.calls.length, 0, 'values that are not X keys are never sent anywhere');
        assert.deepEqual(await attempt({apiKey: keys.apiKey}), {ok: false, reason: 'not-found'}, 'every field is required');
        assert.deepEqual(await attempt({...keys, accessTokenSecret: 'V'.repeat(45)}), {ok: false, reason: 'provider-refused'}, 'X refuses a wrong signature');
        for (const [name, me] of [['an answer without a user', () => Response.json({errors: [{title: 'x'}]})],
            ['another user than the token names', () => Response.json({data: {id: '42', name: 'Other', username: 'other'}})],
            ['a server error', () => new Response('oops', {status: 503})]] as const) {
            fake.me = me;
            assert.deepEqual(await attempt(), {ok: false, reason: 'provider-refused'}, name);
        }
    });
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_credentials').first<number>('n'), 0);
}));

test('xLength_WeightsLikeX_CjkEmojiAndLinks', () => {
    const cases: [string, number, string][] = [
        ['hello', 5, 'Latin counts 1'], ['你好', 4, 'CJK counts 2'], ['e\u0301', 1, 'text is NFC-normalized first'], ['“quoted” — dash', 15, 'common punctuation counts 1'],
        ['👨\u200d👩\u200d👧\u200d👦', 2, 'an emoji sequence counts 2'], ['❤\ufe0f 👍🏽', 5, 'a variation selector and a skin tone stay with their emoji'],
        ['🇦🇺 1\ufe0f\u20e3 #\ufe0f\u20e3', 8, 'a flag and keycaps count 2 each'], ['🏴󠁧󠁢󠁳󠁣󠁴󠁿', 2, 'a tag flag counts 2'],
        ['😀' + '\u0301'.repeat(30), 32, 'marks stacked on an emoji are counted'], ['😀' + '\u200c'.repeat(30), 32, 'joiners on an emoji are counted'],
        ['😀\u200d'.repeat(5) + '😀', 17, 'more pictographs than an emoji has are counted one by one'], ['a\u20e3'.repeat(10), 30, 'a keycap mark on a letter is not a keycap'],
        ['see https://example.com/a/very/long/path/that/is/longer/than/twenty-three', 4 + 23, 'a link counts 23 whatever its length'],
        ['(see http://x.co).', 1 + 4 + 23 + 2, 'brackets and trailing punctuation around a link count on their own'],
        ['https://x.co/a' + 'あ'.repeat(100), 23 + 200, 'a link ends at the first character that is not a URL character'],
        ['詳細はhttps://example.comをご覧ください', 6 + 23 + 14, 'a link inside CJK text counts 23'], ['「https://example.com」', 2 + 23 + 2, 'CJK brackets count on their own'],
        ['see:https://x.co', 4 + 23, 'a scheme after punctuation starts a link'], ['seehttps://x.co/' + 'a'.repeat(20), 36, 'a scheme joined to a word is not a link'],
        ['https://' + 'a'.repeat(30), 38, 'a scheme without a domain is not a link'], ['https://例え.co.jp', 23, 'a link to an international host counts at least 23'], ['https://例え.中国', 23, 'so does one with an international top-level domain'],
        ['x.co', 23, 'a bare domain counts at least 23'], ['例え.jp', 23, 'an international domain counts at least 23'], ['node.js 3.5', 23 + 1 + 3, 'a number is not a domain']];
    for (const [text, weight, why] of cases) assert.equal(xLength(text), weight, why);
    assert.ok(xLength('a'.repeat(281)) > 280 && xLength('https://x.co/a' + 'あ'.repeat(200)) > 280, 'counting past 280 only says the text is too long');
    for (const long of [' a'.repeat(500_000), 'あa'.repeat(500_000), '(https://x.co)'.repeat(70_000), 'a'.repeat(1_000_000), '\u0301'.repeat(1_000_000)]) {
        const started = performance.now();
        assert.ok(xLength(long) > 280);
        assert.ok(performance.now() - started < 1000, 'counting a megabyte stays fast: ' + JSON.stringify(long.slice(0, 6)));
    }
});

test('xRules_Limits_TextImagesAltNoSchedule', async () => {
    const check = (post: unknown) => publishingProvider(x).checkContent!(undefined as never, {accountId: '', operation: 'publish_post', content: JSON.stringify(post), targetAccount: userId});
    const file = (bytes = 1000) => ({mediaId: crypto.randomUUID(), bytes, sha256: 'a'.repeat(64)});
    assert.equal(await check({text: 'a'.repeat(280), media: []}), null);
    const cases: [unknown, RegExp][] = [[{text: 'a'.repeat(281), media: []}, /at most 280 weighted characters/], [{text: '你'.repeat(141), media: []}, /at most 280 weighted/],
        [{text: 'a'.repeat(258) + ' https://example.com', media: []}, /at most 280 weighted/],
        [{text: 'x', media: Array.from({length: 5}, () => file())}, /at most 4 media/], [{text: 'x', media: [{...file(), alt: 'a'.repeat(1001)}]}, /alt must be at most 1000 characters here/],
        [{text: 'x', media: [], publishAt: new Date(Date.now() + 3600_000).toISOString().replace(/\.\d{3}Z$/, 'Z')}, /publishAt is not supported/],
        [{text: 'x', media: [], settings: {reply: true}}, /settings are not taken/], [{text: '', media: []}, /text or media is required/]];
    for (const [post, reason] of cases) assert.match((await check(post)) ?? '', reason, JSON.stringify(post).slice(0, 70));
    const description = publishingProvider(x).describeContent(userId);
    assert.ok(description.includes('at most 280 weighted characters') && !description.includes('takes none'), 'agents are told how X counts and that alt is taken');
});

test('executeXPost_Text_SignedAndPostedOnce', () => scenario(async ({h, fake, env}) => {
    const {agent, actionId} = await approvedPost(h, env, fake, {text: 'New drop tonight 🎧', media: []});
    assert.deepEqual(tweets(fake), [], 'approval alone posts nothing');
    const first = await execute(h, agent, actionId), again = await execute(h, agent, actionId);
    assert.deepEqual([first.status, first.receipt, again.receipt], ['completed', '1860000000000000001', '1860000000000000001']);
    assert.equal(tweets(fake).length, 1, 'posted exactly once');
    assert.ok(tweets(fake)[0].signed, 'the post is signed with the owner\'s keys');
    assert.deepEqual(tweets(fake)[0].json, {text: 'New drop tonight 🎧'});
}));

test('executeXPost_Images_UploadExactBytesThenAttach', () => scenario(async ({h, fake, env}) => {
    const png = randomBytes(4000), jpeg = randomBytes(6000);
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) =>
        ({text: '', media: [await upload(h, agent, png, 'image/png'), await upload(h, agent, jpeg, 'image/jpeg')]}));
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    const uploads = fake.calls.filter(call => call.path === '/2/media/upload');
    assert.deepEqual(uploads.map(call => [call.signed, call.upload!.category, call.upload!.type]), [[true, 'tweet_image', 'image/png'], [true, 'tweet_image', 'image/jpeg']]);
    assert.ok(uploads[0].upload!.bytes.equals(png) && uploads[1].upload!.bytes.equals(jpeg), 'the exact stored bytes are uploaded');
    assert.deepEqual(tweets(fake)[0].json, {media: {media_ids: ['1710000000000000001', '1710000000000000002']}}, 'an empty text is omitted');
}));

test('executeXPost_ImagesWithAlt_AltSetOnTheUploadedMediaBeforePosting', () => scenario(async ({h, fake, env}) => {
    const alt = '  Album cover:\n' + 'k'.repeat(985);
    assert.equal(alt.length, 1000, 'the longest alt text X takes');
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) =>
        ({text: 'cover', media: [{...await upload(h, agent, randomBytes(100), 'image/png'), alt}, await upload(h, agent, randomBytes(200), 'image/jpeg')]}));
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    const metadata = fake.calls.filter(call => call.path === '/2/media/metadata');
    assert.deepEqual(metadata.map(call => [call.signed, call.json]), [[true, {id: '1710000000000000001', metadata: {alt_text: {text: alt}}}]],
        'alt is set once, exactly as approved, only on the file that has it');
    const order = fake.calls.map(call => call.path).filter(path => path !== '/2/users/me');
    assert.ok(order.indexOf('/2/media/metadata') < order.indexOf('/2/tweets'), 'alt is set before anything is posted');
    assert.deepEqual(tweets(fake)[0].json, {text: 'cover', media: {media_ids: ['1710000000000000001', '1710000000000000002']}});
}));

test('executeXPost_AltRefusedOrUnanswered_FailedWithNothingPosted', () => scenario(async ({h, fake, env}) => {
    for (const [name, answer] of [['refused', () => Response.json({title: 'Invalid Request'}, {status: 400})], ['server error', () => new Response('oops', {status: 503})],
        ['another media id', () => Response.json({data: {id: '1710000000000000999'}})],
        ['not a 200', (call: Call) => Response.json({data: {id: call.json.id}}, {status: 202})], ['network failure', () => { throw Error('connection reset'); }]] as const) {
        fake.metadata = answer as (call: Call) => Response;
        const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) =>
            ({text: 'cover', media: [{...await upload(h, agent, randomBytes(100), 'image/webp'), alt: 'a photo'}]}));
        const result = await execute(h, agent, actionId);
        assert.deepEqual([result.status, result.failure], ['failed', 'provider-rejected: media-alt-refused'], name);
        assert.equal(tweets(fake).length, 0, 'no post is created when alt text is not set: ' + name);
    }
}));

test('executeXPost_SecondImageAltRefused_FailedWithNothingPosted', () => scenario(async ({h, fake, env}) => {
    let metadataCalls = 0;
    fake.metadata = (call: Call) => ++metadataCalls === 1 ? Response.json({data: {id: call.json.id}}) : new Response('oops', {status: 503});
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) => ({text: 'two', media: [
        {...await upload(h, agent, randomBytes(100), 'image/png'), alt: 'first'}, {...await upload(h, agent, randomBytes(100), 'image/png'), alt: 'second'}]}));
    const result = await execute(h, agent, actionId);
    assert.deepEqual([result.status, result.failure], ['failed', 'provider-rejected: media-alt-refused']);
    assert.equal(metadataCalls, 2, 'the first alt text was set and the second refused');
    assert.equal(tweets(fake).length, 0, 'nothing is posted when a later image loses its alt text');
}));

test('executeXPost_EmptyAlt_NoMetadataCall', () => scenario(async ({h, fake, env}) => {
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) =>
        ({text: 'plain', media: [{...await upload(h, agent, randomBytes(100), 'image/png'), alt: ''}]}));
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    assert.equal(fake.calls.filter(call => call.path === '/2/media/metadata').length, 0, 'an empty alt text is not sent');
    assert.equal(tweets(fake).length, 1);
}));

test('executeXPost_UploadRefusedOrUnanswered_FailedWithNothingPosted', () => scenario(async ({h, fake, env}) => {
    let uploads = 0;
    for (const [name, answer] of [['refused', () => Response.json({title: 'Invalid media'}, {status: 400})], ['server error', () => new Response('oops', {status: 503})],
        ['no media id', () => Response.json({data: {media_key: '3_1'}})], ['network failure', () => { throw Error('connection reset'); }],
        ['the second image', () => ++uploads % 2 === 0 ? Response.json({title: 'Invalid media'}, {status: 400}) : Response.json({data: {id: '1710000000000000009'}})]] as const) {
        fake.upload = answer;
        const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) =>
            ({text: 'cover', media: [await upload(h, agent, randomBytes(100), 'image/webp'), await upload(h, agent, randomBytes(200), 'image/png')]}));
        const result = await execute(h, agent, actionId);
        assert.deepEqual([result.status, result.failure], ['failed', 'provider-rejected: media-upload-refused'], name);
        assert.equal(tweets(fake).length, 0, 'no post is created when a file is not uploaded: ' + name);
    }
}));

test('executeXPost_RefusedOrUncertain_FailedOrUnknownAndNeverResent', () => scenario(async ({h, fake, env}) => {
    for (const [status, expected] of [[400, 'failed'], [401, 'failed'], [403, 'failed'], [429, 'failed'], [500, 'unknown'], [503, 'unknown']] as const) {
        fake.answer = () => Response.json({title: 'refused', status}, {status});
        const {agent, actionId} = await approvedPost(h, env, fake, {text: 'status ' + status, media: []});
        const result = await execute(h, agent, actionId);
        assert.equal(result.status, expected, String(status));
        if (expected === 'failed') assert.equal(result.failure, 'provider-rejected: x-refused-' + status);
        fake.answer = undefined;
        await execute(h, agent, actionId);
        assert.equal(tweets(fake).length, 1, 'never sent again: ' + status);
        fake.calls.length = 0;
    }
    // A 201 without a post id, a 200 or a lost connection is not a confirmation that this post was published.
    for (const answer of [() => Response.json({data: {text: 'x'}}, {status: 201}), () => Response.json({data: {id: '1860000000000000001'}}),
        () => { throw Error('connection reset'); }]) {
        fake.answer = answer;
        const {agent, actionId} = await approvedPost(h, env, fake, {text: 'unconfirmed', media: []});
        assert.equal((await execute(h, agent, actionId)).status, 'unknown');
        fake.answer = undefined;
        await execute(h, agent, actionId);
        assert.equal(tweets(fake).length, 1, 'an unconfirmed post is never sent again');
        fake.calls.length = 0;
    }
}));
