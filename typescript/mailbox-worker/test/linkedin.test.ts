import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { connectAccount } from '../src/external-credentials.ts';
import { openSecret } from '../src/credential-vault.ts';
import { linkedin, linkedinLength, littleText } from '../src/linkedin-provider.ts';
import { publishingProvider } from '../src/publishing.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const key = randomBytes(32).toString('base64url');
const member = 'xY12_ab-CD', author = 'urn:li:person:' + member;
const accessToken = 'AQV' + 'a1B2-c3_D4.e5~'.repeat(20);
const environment = (h: Harness) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
    REGISTRATION_GLOBAL_PER_HOUR: '100', EXTERNAL_CREDENTIAL_KEY: key}) as unknown as Env;
const connectLinkedIn = (env: Env, {fields = {accessToken}, ...owner}: {humanId: string; authAt: number; agentId: string; fields?: Record<string, string>}) =>
    connectAccount(env, {...owner, provider: 'linkedin', fields});

type Call = {method: string; url: URL; bearer: boolean; version: string | null; restli: string | null; json?: any; bytes?: Buffer; type?: string | null};
/** A LinkedIn API fake: the member's userinfo, image upload initialisation, the upload host and posts. */
type Fake = {calls: Call[]; token?: string; me?: () => Response; init?: (call: Call) => Response; put?: (call: Call) => Response; answer?: (call: Call) => Response};
function linkedinApi(fake: Fake) {
    let image = 0;
    return async (request: Request): Promise<Response> => {
        const url = new URL(request.url);
        const call: Call = {method: request.method, url, bearer: request.headers.get('Authorization') === 'Bearer ' + (fake.token ?? accessToken),
            version: request.headers.get('LinkedIn-Version'), restli: request.headers.get('X-Restli-Protocol-Version')};
        fake.calls.push(call);
        if (!call.bearer) return Response.json({status: 401, serviceErrorCode: 65600}, {status: 401});
        if (url.origin === 'https://www.linkedin.com' && url.pathname.startsWith('/dms-uploads/') && request.method === 'PUT') {
            call.bytes = Buffer.from(await request.arrayBuffer()); call.type = request.headers.get('Content-Type');
            return fake.put?.(call) ?? new Response(null, {status: 201});
        }
        if (url.origin !== 'https://api.linkedin.com') return new Response('not found', {status: 404});
        if (request.method === 'GET' && url.pathname === '/v2/userinfo') return fake.me?.() ?? Response.json({sub: member, name: 'HSC Beats', email_verified: true});
        if (call.version !== '202610' || call.restli !== '2.0.0') return Response.json({status: 426, message: 'version required'}, {status: 426});
        if (request.method === 'POST' && url.pathname === '/rest/images' && url.search === '?action=initializeUpload') {
            call.json = await request.json();
            const id = 'C4E10AQ' + String(++image).padStart(5, '0');
            return fake.init?.(call) ?? Response.json({value: {uploadUrlExpiresAt: Date.now() + 3600_000, uploadUrl: `https://www.linkedin.com/dms-uploads/${id}/uploaded-image/0`, image: 'urn:li:image:' + id}});
        }
        if (request.method === 'POST' && url.pathname === '/rest/posts') {
            call.json = await request.json();
            return fake.answer?.(call) ?? new Response(null, {status: 201, headers: {'x-restli-id': 'urn:li:share:6844785523593134080'}});
        }
        return new Response('not found', {status: 404});
    };
}
async function scenario(run: (s: {h: Harness; fake: Fake; env: Env}) => Promise<void>) {
    const fake: Fake = {calls: []};
    const h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key}, outboundService: linkedinApi(fake)});
    try { await run({h, fake, env: environment(h)}); } finally { await h.close(); }
}
/** Runs connecting in this process against the same fake. */
async function withLinkedIn(fake: Fake, run: () => Promise<void>) {
    const original = globalThis.fetch, handler = linkedinApi(fake);
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        assert.equal(init?.redirect, 'manual');
        return handler(new Request(String(input), init));
    };
    try { await run(); } finally { globalThis.fetch = original; }
}
const ref = 'linkedin:' + member;
async function connected(h: Harness, env: Env, fake: Fake) {
    const agent = await h.register(), humanId = await h.claim(agent);
    await withLinkedIn(fake, async () => {
        assert.deepEqual(await connectLinkedIn(env, {humanId, authAt: Date.now(), agentId: agent.accountId}), {ok: true, accounts: ['HSC Beats']});
    });
    fake.calls.length = 0;
    return {agent, humanId};
}
const propose = (h: Harness, agent: any, content: unknown) => h.request('/v1/external-actions', agent, 'POST', {credentialRef: ref, operation: 'publish_post', content: JSON.stringify(content)});
async function approvedPost(h: Harness, env: Env, fake: Fake, content: unknown | ((agent: any) => Promise<unknown>)) {
    const {agent, humanId} = await connected(h, env, fake);
    const response = await propose(h, agent, typeof content === 'function' ? await (content as (a: any) => Promise<unknown>)(agent) : content);
    assert.equal(response.status, 200, await response.clone().text());
    const {actionId} = await response.json() as {actionId: string};
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    assert.deepEqual(await ownerAction(env, {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: actionId,
        planDigest: overview.externalActions.find(a => a.actionId === actionId)!.planDigest} as OwnerAction), {ok: true});
    return {agent, actionId};
}
const execute = (h: Harness, agent: any, actionId: string) => h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {});
const upload = async (h: Harness, agent: any, bytes: Buffer, type: string) => {
    const media = await (await h.fetchAs(agent, 'http://localhost/v1/external-media', {method: 'PUT', headers: {'Content-Type': type, 'Content-Length': String(bytes.length)}, body: bytes}))
        .json() as {mediaId: string; bytes: number; sha256: string};
    return {mediaId: media.mediaId, bytes: media.bytes, sha256: media.sha256};
};
const posts = (fake: Fake) => fake.calls.filter(call => call.url.pathname === '/rest/posts');
const feed = {visibility: 'PUBLIC', distribution: {feedDistribution: 'MAIN_FEED', targetEntities: [], thirdPartyDistributionChannels: []},
    lifecycleState: 'PUBLISHED', isReshareDisabledByAuthor: false};

test('connectLinkedIn_MemberToken_StoresMemberAndSealsToken', () => scenario(async ({h, fake, env}) => {
    const {agent, humanId} = await connected(h, env, fake);
    const row = (await h.db.prepare('SELECT credential_ref,provider,revision,target_account,target_display,secret FROM external_credentials WHERE account_id=?').bind(agent.accountId).first<any>())!;
    assert.deepEqual([row.credential_ref, row.provider, row.target_account, row.target_display], [ref, 'linkedin', author, 'HSC Beats']);
    const sealed = row.secret instanceof ArrayBuffer ? new Uint8Array(row.secret) : Uint8Array.from(row.secret);
    assert.ok(!Buffer.from(sealed).toString('latin1').includes(accessToken.slice(-30)), 'the token is not stored in clear');
    assert.equal(await openSecret(key, {accountId: agent.accountId, credentialRef: ref, provider: 'linkedin', revision: row.revision}, sealed), accessToken);
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    const form = overview.connectors.find(connector => connector.provider === 'linkedin')!;
    assert.deepEqual(form.fields.map(field => [field.name, field.secret]), [['accessToken', true]]);
    assert.match(form.help ?? '', /w_member_social/);
}));

test('connectLinkedIn_MalformedOrRefusedToken_NothingStored', () => scenario(async ({h, fake, env}) => {
    const agent = await h.register(), humanId = await h.claim(agent);
    const attempt = (fields: Record<string, string> = {accessToken}) => connectLinkedIn(env, {humanId, authAt: Date.now(), agentId: agent.accountId, fields});
    await withLinkedIn(fake, async () => {
        assert.deepEqual(await attempt({accessToken: 'my password!'}), {ok: false, reason: 'provider-refused'}, 'a password is not a token');
        assert.equal(fake.calls.length, 0, 'a value that is not a token is never sent anywhere');
        assert.deepEqual(await attempt({accessToken: 'AQV' + 'z'.repeat(60)}), {ok: false, reason: 'provider-refused'}, 'LinkedIn refuses another token');
        for (const [name, me] of [['an answer without a member', () => Response.json({name: 'Someone'})],
            ['a member id that is not an id', () => Response.json({sub: 'urn:li:person:../x', name: 'x'})],
            ['a server error', () => new Response('oops', {status: 503})]] as const) {
            fake.me = me;
            assert.deepEqual(await attempt(), {ok: false, reason: 'provider-refused'}, name);
        }
    });
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_credentials').first<number>('n'), 0);
}));

test('connectLinkedIn_TokenUpToLinkedInsPlannedLength_StoredWhole', () => scenario(async ({h, fake, env}) => {
    // LinkedIn: tokens are ~500 characters; apps should handle at least 1000 for future expansion.
    for (const length of [1000, 2000]) {
        const agent = await h.register(), humanId = await h.claim(agent), long = 'AQV' + 'a1B2-c3_D4.e5~'.repeat(200).slice(0, length - 3);
        fake.token = long;
        await withLinkedIn(fake, async () => {
            assert.deepEqual(await connectLinkedIn(env, {humanId, authAt: Date.now(), agentId: agent.accountId, fields: {accessToken: long}}), {ok: true, accounts: ['HSC Beats']}, String(length));
        });
        const row = (await h.db.prepare('SELECT revision,secret FROM external_credentials WHERE account_id=?').bind(agent.accountId).first<any>())!;
        const sealed = row.secret instanceof ArrayBuffer ? new Uint8Array(row.secret) : Uint8Array.from(row.secret);
        assert.equal(await openSecret(key, {accountId: agent.accountId, credentialRef: ref, provider: 'linkedin', revision: row.revision}, sealed), long, 'the whole token is sealed');
    }
    const agent = await h.register(), humanId = await h.claim(agent);
    fake.calls.length = 0;
    await withLinkedIn(fake, async () => {
        assert.notEqual((await connectLinkedIn(env, {humanId, authAt: Date.now(), agentId: agent.accountId, fields: {accessToken: 'AQV' + 'a'.repeat(1998)}})).ok, true, 'over 2000 characters');
        assert.deepEqual(await connectLinkedIn(env, {humanId, authAt: Date.now(), agentId: agent.accountId, fields: {accessToken: 'a'.repeat(2049)}}),
            {ok: false, reason: 'not-found'}, 'the router takes field values of at most 2048 characters');
    });
    // The two optional '=' padding characters count toward the 2000-character total limit.
    for (const length of [2001, 2002]) {
        const padded = 'AQV' + 'a'.repeat(length - 5) + '==';
        assert.equal(padded.length, length);
        fake.token = padded;
        await withLinkedIn(fake, async () => {
            assert.notEqual((await connectLinkedIn(env, {humanId, authAt: Date.now(), agentId: agent.accountId,
                fields: {accessToken: padded}})).ok, true, 'reject the padded ' + length + '-character token');
        });
    }
    assert.equal(fake.calls.length, 0, 'an overlong value is never sent to LinkedIn');
}));

test('connectLinkedIn_OwnerHtmlPattern_VModeWorksAndCountsPadding', () => {
    // HTML input pattern attributes use UnicodeSets ('v') semantics: '/' must be escaped inside the class.
    const pattern = linkedin.connector.fields.find(field => field.name === 'accessToken')?.pattern;
    assert.ok(pattern);
    const html = new RegExp('^(?:' + pattern + ')
    assert.equal(littleText('Hi @team (see #launch) *now* a_b ~ [x] {y} <z> | \\'),
        'Hi \\@team \\(see \\#launch\\) \\*now\\* a\\_b \\~ \\[x\\] \\{y\\} \\<z\\> \\| \\\\');
    assert.equal(littleText('plain text, 你好 🎧!'), 'plain text, 你好 🎧!', 'everything else is sent as written');
    assert.equal(linkedinLength('#a'), 3);
    assert.equal(linkedinLength('a'.repeat(10)), 10);
});

test('linkedinRules_Limits_TextImagesNoSchedule', async () => {
    const check = (post: unknown) => publishingProvider(linkedin).checkContent!(undefined as never, {accountId: '', operation: 'publish_post', content: JSON.stringify(post), targetAccount: author});
    const file = (bytes = 1000) => ({mediaId: crypto.randomUUID(), bytes, sha256: 'a'.repeat(64)});
    assert.equal(await check({text: 'a'.repeat(3000), media: []}), null);
    const cases: [unknown, RegExp][] = [[{text: 'a'.repeat(3001), media: []}, /at most 3000 weighted characters/],
        [{text: 'a'.repeat(2999) + '#', media: []}, /at most 3000 weighted characters/],
        [{text: 'x', media: Array.from({length: 10}, () => file())}, /at most 9 media/],
        [{text: 'x', media: [], publishAt: new Date(Date.now() + 3600_000).toISOString().replace(/\.\d{3}Z$/, 'Z')}, /publishAt is not supported/],
        [{text: 'x', media: [], settings: {visibility: 'CONNECTIONS'}}, /settings are not taken/], [{text: '', media: []}, /text or media is required/]];
    for (const [post, reason] of cases) assert.match((await check(post)) ?? '', reason, JSON.stringify(post).slice(0, 70));
    assert.ok(publishingProvider(linkedin).describeContent(author).includes('at most 3000 weighted characters'));
});

test('executeLinkedInPost_Text_EscapedVersionedAndPostedOnce', () => scenario(async ({h, fake, env}) => {
    const {agent, actionId} = await approvedPost(h, env, fake, {text: 'Launch day #1 (finally) @everyone', media: []});
    assert.deepEqual(posts(fake), [], 'approval alone posts nothing');
    const first = await execute(h, agent, actionId), again = await execute(h, agent, actionId);
    assert.deepEqual([first.status, first.receipt, again.receipt], ['completed', 'urn:li:share:6844785523593134080', 'urn:li:share:6844785523593134080']);
    assert.equal(posts(fake).length, 1, 'posted exactly once');
    const [call] = posts(fake);
    assert.deepEqual([call.bearer, call.version, call.restli], [true, '202610', '2.0.0']);
    assert.deepEqual(call.json, {author, commentary: 'Launch day \\#1 \\(finally\\) \\@everyone', ...feed}, 'the approved text is sent as plain text, never as markup');
}));

test('executeLinkedInPost_Images_UploadExactBytesThenAttachWithAlt', () => scenario(async ({h, fake, env}) => {
    const png = randomBytes(4000), jpeg = randomBytes(6000);
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) =>
        ({text: '', media: [{...await upload(h, agent, png, 'image/png'), alt: 'A drum machine'}, {...await upload(h, agent, jpeg, 'image/jpeg'), alt: ''}]}));
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    const inits = fake.calls.filter(call => call.url.pathname === '/rest/images');
    assert.deepEqual(inits.map(call => call.json), [{initializeUploadRequest: {owner: author}}, {initializeUploadRequest: {owner: author}}]);
    const puts = fake.calls.filter(call => call.method === 'PUT');
    assert.deepEqual(puts.map(call => [call.bearer, call.type]), [[true, 'image/png'], [true, 'image/jpeg']]);
    assert.ok(puts[0].bytes!.equals(png) && puts[1].bytes!.equals(jpeg), 'the exact stored bytes are uploaded');
    assert.deepEqual(posts(fake)[0].json, {author, commentary: '', ...feed,
        content: {multiImage: {images: [{id: 'urn:li:image:C4E10AQ00001', altText: 'A drum machine'}, {id: 'urn:li:image:C4E10AQ00002'}]}}}, 'an empty alt text is not sent');
}));

test('executeLinkedInPost_OneImage_SentAsMedia', () => scenario(async ({h, fake, env}) => {
    const gif = randomBytes(3000);
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) => ({text: 'One', media: [{...await upload(h, agent, gif, 'image/gif'), alt: 'Loop'}]}));
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    assert.deepEqual(posts(fake)[0].json.content, {media: {id: 'urn:li:image:C4E10AQ00001', altText: 'Loop'}});
}));

test('executeLinkedInPost_UploadFailures_RejectedWithNothingPosted', () => scenario(async ({h, fake, env}) => {
    const cases: [string, Partial<Fake>][] = [
        ['a refused initialisation', {init: () => Response.json({status: 403}, {status: 403})}],
        ['an upload address off LinkedIn', {init: () => Response.json({value: {uploadUrl: 'https://evil.example/dms-uploads/x', image: 'urn:li:image:C1'}})}],
        ['a plain-HTTP upload address', {init: () => Response.json({value: {uploadUrl: 'http://www.linkedin.com/dms-uploads/x', image: 'urn:li:image:C1'}})}],
        ['a look-alike host', {init: () => Response.json({value: {uploadUrl: 'https://www.linkedin.com.evil.example/dms-uploads/x', image: 'urn:li:image:C1'}})}],
        ['an answer without an image', {init: () => Response.json({value: {uploadUrl: 'https://www.linkedin.com/dms-uploads/x'}})}],
        ['an answer naming something other than an image', {init: () => Response.json({value: {uploadUrl: 'https://www.linkedin.com/dms-uploads/x', image: 'urn:li:share:1'}})}],
        ['a refused upload', {put: () => new Response('no', {status: 400})}],
        ['a redirected upload, which is not followed', {put: () => new Response(null, {status: 307, headers: {location: 'https://evil.example/dms-uploads/x'}})}],
        ['a dropped upload connection', {put: () => { throw new TypeError('network connection lost'); }}]];
    for (const [name, override] of cases) {
        fake.init = override.init; fake.put = override.put;
        const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) => ({text: 'x', media: [await upload(h, agent, randomBytes(500), 'image/png')]}));
        const outcome = await execute(h, agent, actionId);
        assert.deepEqual([outcome.status, outcome.failure], ['failed', 'provider-rejected: media-upload-refused'], name);
        assert.deepEqual(posts(fake), [], name + ': nothing is posted');
        assert.ok(!fake.calls.some(call => call.url.hostname.endsWith('evil.example') || call.url.protocol !== 'https:'), name + ': the token never leaves LinkedIn\'s HTTPS hosts');
        assert.ok(!fake.calls.some(call => call.method === 'PUT' && call.url.pathname === '/dms-uploads/x'), name + ': nothing is uploaded to a refused address');
    }
}));

test('executeLinkedInPost_DefinitePostRefusals_Rejected', () => scenario(async ({h, fake, env}) => {
    for (const status of [400, 401, 403, 422, 426, 429]) {
        fake.answer = () => Response.json({status}, {status});
        const {agent, actionId} = await approvedPost(h, env, fake, {text: 'refused ' + status, media: []});
        const outcome = await execute(h, agent, actionId);
        assert.deepEqual([outcome.status, outcome.failure], ['failed', 'provider-rejected: linkedin-refused-' + status]);
    }
}));

test('executeLinkedInPost_UncertainPostOutcomes_UnknownAndNeverResent', () => scenario(async ({h, fake, env}) => {
    for (const [name, answer] of [['a server error', () => new Response('oops', {status: 500})],
        ['a 201 without an id', () => new Response(null, {status: 201})],
        ['a redirect, which is not followed', () => new Response(null, {status: 302, headers: {location: 'https://www.linkedin.com/feed/'}})],
        ['a dropped connection', () => { throw new TypeError('network connection lost'); }],
        ['a 201 with another kind of id', () => new Response(null, {status: 201, headers: {'x-restli-id': 'urn:li:image:C1'}})]] as const) {
        fake.answer = answer;
        const {agent, actionId} = await approvedPost(h, env, fake, {text: name, media: []});
        assert.equal((await execute(h, agent, actionId)).status, 'unknown', name);
        const sent = posts(fake).length;
        assert.equal((await execute(h, agent, actionId)).status, 'unknown', name + ': stays unknown');
        assert.equal(posts(fake).length, sent, name + ': never sent again');
    }
}));
, 'v');
    for (const length of [1000, 2000]) {
        const value = 'AQV' + 'a'.repeat(length - 5) + '==';
        assert.equal(html.test(value), true, 'allow a valid padded token of total length ' + length);
    }
    for (const length of [2001, 2002]) {
        const value = 'AQV' + 'a'.repeat(length - 5) + '==';
        assert.equal(html.test(value), false, 'reject padded token of total length ' + length);
    }
});

test('littleText_ReservedCharacters_EscapedAndCountedTwice', () => {
    assert.equal(littleText('Hi @team (see #launch) *now* a_b ~ [x] {y} <z> | \\'),
        'Hi \\@team \\(see \\#launch\\) \\*now\\* a\\_b \\~ \\[x\\] \\{y\\} \\<z\\> \\| \\\\');
    assert.equal(littleText('plain text, 你好 🎧!'), 'plain text, 你好 🎧!', 'everything else is sent as written');
    assert.equal(linkedinLength('#a'), 3);
    assert.equal(linkedinLength('a'.repeat(10)), 10);
});

test('linkedinRules_Limits_TextImagesNoSchedule', async () => {
    const check = (post: unknown) => publishingProvider(linkedin).checkContent!(undefined as never, {accountId: '', operation: 'publish_post', content: JSON.stringify(post), targetAccount: author});
    const file = (bytes = 1000) => ({mediaId: crypto.randomUUID(), bytes, sha256: 'a'.repeat(64)});
    assert.equal(await check({text: 'a'.repeat(3000), media: []}), null);
    const cases: [unknown, RegExp][] = [[{text: 'a'.repeat(3001), media: []}, /at most 3000 weighted characters/],
        [{text: 'a'.repeat(2999) + '#', media: []}, /at most 3000 weighted characters/],
        [{text: 'x', media: Array.from({length: 10}, () => file())}, /at most 9 media/],
        [{text: 'x', media: [], publishAt: new Date(Date.now() + 3600_000).toISOString().replace(/\.\d{3}Z$/, 'Z')}, /publishAt is not supported/],
        [{text: 'x', media: [], settings: {visibility: 'CONNECTIONS'}}, /settings are not taken/], [{text: '', media: []}, /text or media is required/]];
    for (const [post, reason] of cases) assert.match((await check(post)) ?? '', reason, JSON.stringify(post).slice(0, 70));
    assert.ok(publishingProvider(linkedin).describeContent(author).includes('at most 3000 weighted characters'));
});

test('executeLinkedInPost_Text_EscapedVersionedAndPostedOnce', () => scenario(async ({h, fake, env}) => {
    const {agent, actionId} = await approvedPost(h, env, fake, {text: 'Launch day #1 (finally) @everyone', media: []});
    assert.deepEqual(posts(fake), [], 'approval alone posts nothing');
    const first = await execute(h, agent, actionId), again = await execute(h, agent, actionId);
    assert.deepEqual([first.status, first.receipt, again.receipt], ['completed', 'urn:li:share:6844785523593134080', 'urn:li:share:6844785523593134080']);
    assert.equal(posts(fake).length, 1, 'posted exactly once');
    const [call] = posts(fake);
    assert.deepEqual([call.bearer, call.version, call.restli], [true, '202610', '2.0.0']);
    assert.deepEqual(call.json, {author, commentary: 'Launch day \\#1 \\(finally\\) \\@everyone', ...feed}, 'the approved text is sent as plain text, never as markup');
}));

test('executeLinkedInPost_Images_UploadExactBytesThenAttachWithAlt', () => scenario(async ({h, fake, env}) => {
    const png = randomBytes(4000), jpeg = randomBytes(6000);
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) =>
        ({text: '', media: [{...await upload(h, agent, png, 'image/png'), alt: 'A drum machine'}, {...await upload(h, agent, jpeg, 'image/jpeg'), alt: ''}]}));
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    const inits = fake.calls.filter(call => call.url.pathname === '/rest/images');
    assert.deepEqual(inits.map(call => call.json), [{initializeUploadRequest: {owner: author}}, {initializeUploadRequest: {owner: author}}]);
    const puts = fake.calls.filter(call => call.method === 'PUT');
    assert.deepEqual(puts.map(call => [call.bearer, call.type]), [[true, 'image/png'], [true, 'image/jpeg']]);
    assert.ok(puts[0].bytes!.equals(png) && puts[1].bytes!.equals(jpeg), 'the exact stored bytes are uploaded');
    assert.deepEqual(posts(fake)[0].json, {author, commentary: '', ...feed,
        content: {multiImage: {images: [{id: 'urn:li:image:C4E10AQ00001', altText: 'A drum machine'}, {id: 'urn:li:image:C4E10AQ00002'}]}}}, 'an empty alt text is not sent');
}));

test('executeLinkedInPost_OneImage_SentAsMedia', () => scenario(async ({h, fake, env}) => {
    const gif = randomBytes(3000);
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) => ({text: 'One', media: [{...await upload(h, agent, gif, 'image/gif'), alt: 'Loop'}]}));
    assert.equal((await execute(h, agent, actionId)).status, 'completed');
    assert.deepEqual(posts(fake)[0].json.content, {media: {id: 'urn:li:image:C4E10AQ00001', altText: 'Loop'}});
}));

test('executeLinkedInPost_UploadFailures_RejectedWithNothingPosted', () => scenario(async ({h, fake, env}) => {
    const cases: [string, Partial<Fake>][] = [
        ['a refused initialisation', {init: () => Response.json({status: 403}, {status: 403})}],
        ['an upload address off LinkedIn', {init: () => Response.json({value: {uploadUrl: 'https://evil.example/dms-uploads/x', image: 'urn:li:image:C1'}})}],
        ['a plain-HTTP upload address', {init: () => Response.json({value: {uploadUrl: 'http://www.linkedin.com/dms-uploads/x', image: 'urn:li:image:C1'}})}],
        ['a look-alike host', {init: () => Response.json({value: {uploadUrl: 'https://www.linkedin.com.evil.example/dms-uploads/x', image: 'urn:li:image:C1'}})}],
        ['an answer without an image', {init: () => Response.json({value: {uploadUrl: 'https://www.linkedin.com/dms-uploads/x'}})}],
        ['an answer naming something other than an image', {init: () => Response.json({value: {uploadUrl: 'https://www.linkedin.com/dms-uploads/x', image: 'urn:li:share:1'}})}],
        ['a refused upload', {put: () => new Response('no', {status: 400})}],
        ['a redirected upload, which is not followed', {put: () => new Response(null, {status: 307, headers: {location: 'https://evil.example/dms-uploads/x'}})}],
        ['a dropped upload connection', {put: () => { throw new TypeError('network connection lost'); }}]];
    for (const [name, override] of cases) {
        fake.init = override.init; fake.put = override.put;
        const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) => ({text: 'x', media: [await upload(h, agent, randomBytes(500), 'image/png')]}));
        const outcome = await execute(h, agent, actionId);
        assert.deepEqual([outcome.status, outcome.failure], ['failed', 'provider-rejected: media-upload-refused'], name);
        assert.deepEqual(posts(fake), [], name + ': nothing is posted');
        assert.ok(!fake.calls.some(call => call.url.hostname.endsWith('evil.example') || call.url.protocol !== 'https:'), name + ': the token never leaves LinkedIn\'s HTTPS hosts');
        assert.ok(!fake.calls.some(call => call.method === 'PUT' && call.url.pathname === '/dms-uploads/x'), name + ': nothing is uploaded to a refused address');
    }
}));

test('executeLinkedInPost_DefinitePostRefusals_Rejected', () => scenario(async ({h, fake, env}) => {
    for (const status of [400, 401, 403, 422, 426, 429]) {
        fake.answer = () => Response.json({status}, {status});
        const {agent, actionId} = await approvedPost(h, env, fake, {text: 'refused ' + status, media: []});
        const outcome = await execute(h, agent, actionId);
        assert.deepEqual([outcome.status, outcome.failure], ['failed', 'provider-rejected: linkedin-refused-' + status]);
    }
}));

test('executeLinkedInPost_UncertainPostOutcomes_UnknownAndNeverResent', () => scenario(async ({h, fake, env}) => {
    for (const [name, answer] of [['a server error', () => new Response('oops', {status: 500})],
        ['a 201 without an id', () => new Response(null, {status: 201})],
        ['a redirect, which is not followed', () => new Response(null, {status: 302, headers: {location: 'https://www.linkedin.com/feed/'}})],
        ['a dropped connection', () => { throw new TypeError('network connection lost'); }],
        ['a 201 with another kind of id', () => new Response(null, {status: 201, headers: {'x-restli-id': 'urn:li:image:C1'}})]] as const) {
        fake.answer = answer;
        const {agent, actionId} = await approvedPost(h, env, fake, {text: name, media: []});
        assert.equal((await execute(h, agent, actionId)).status, 'unknown', name);
        const sent = posts(fake).length;
        assert.equal((await execute(h, agent, actionId)).status, 'unknown', name + ': stays unknown');
        assert.equal(posts(fake).length, sent, name + ': never sent again');
    }
}));
