import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { connectAccount } from '../src/external-credentials.ts';
import { openSecret } from '../src/credential-vault.ts';
import { telegram } from '../src/telegram-provider.ts';
import { publishingProvider } from '../src/publishing.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const connectTelegram = (env: Env, {botToken, chat, ...owner}: {humanId: string; authAt: number; agentId: string; botToken: string; chat: string}) =>
    connectAccount(env, {...owner, provider: 'telegram', fields: {botToken, chat}});
const key = randomBytes(32).toString('base64url');
const token = '123456789:' + 'A'.repeat(35), botId = 123456789, chatId = -1001234567890;
const environment = (h: Harness) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
    REGISTRATION_GLOBAL_PER_HOUR: '100', EXTERNAL_CREDENTIAL_KEY: key}) as unknown as Env;
const ok = (result: unknown) => Response.json({ok: true, result});
const refused = (status: number, description = 'Bad Request') => Response.json({ok: false, error_code: status, description}, {status});

type Call = {method: string; tokenInPath: boolean; json?: any; form?: Map<string, string | {type: string; bytes: Buffer}>};
/** A Bot API fake; `answers` overrides one method's reply. Every method answers for the configured token only. */
type Fake = {calls: Call[]; answers: Record<string, (call: Call) => Response>};
function botApi(fake: Fake) {
    return async (request: Request): Promise<Response> => {
        const url = new URL(request.url);
        const match = /^\/bot([^/]+)\/([A-Za-z]+)$/.exec(url.pathname);
        if (url.origin !== 'https://api.telegram.org' || !match) return new Response('refused', {status: 599});
        const call: Call = {method: match[2], tokenInPath: match[1] === token};
        if ((request.headers.get('content-type') ?? '').startsWith('multipart/form-data')) {
            call.form = new Map();
            for (const [name, value] of await request.formData())
                call.form.set(name, typeof value === 'string' ? value : {type: value.type, bytes: Buffer.from(await value.arrayBuffer())});
        } else call.json = await request.json();
        fake.calls.push(call);
        if (!call.tokenInPath) return refused(401, 'Unauthorized');
        if (fake.answers[call.method]) return fake.answers[call.method](call);
        if (call.method === 'getMe') return ok({id: botId, is_bot: true, username: 'hsc_beats_bot'});
        if (call.method === 'getChat') return ok({id: chatId, type: 'channel', title: 'HSC Beats', username: 'hscbeats'});
        if (call.method === 'getChatMember') return ok({status: 'administrator', can_post_messages: true, user: {id: botId}});
        if (call.method === 'sendMediaGroup') return ok(JSON.parse(String(call.form!.get('media'))).map((_: unknown, i: number) => ({message_id: 40 + i})));
        if (['sendMessage', 'sendPhoto', 'sendVideo'].includes(call.method)) return ok({message_id: 40});
        return refused(404, 'Not Found');
    };
}
async function scenario(run: (s: {h: Harness; fake: Fake; env: Env}) => Promise<void>) {
    const fake: Fake = {calls: [], answers: {}};
    const h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key}, outboundService: botApi(fake)});
    try { await run({h, fake, env: environment(h)}); } finally { await h.close(); }
}
/** Runs connectTelegram in this process against the same fake. */
async function withBotApi(fake: Fake, run: () => Promise<void>) {
    const original = globalThis.fetch, handler = botApi(fake);
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        assert.equal(init?.redirect, 'manual');
        return handler(new Request(String(input), init));
    };
    try { await run(); } finally { globalThis.fetch = original; }
}
const ref = 'telegram:' + chatId;
async function connected(h: Harness, env: Env, fake: Fake) {
    const agent = await h.register(), humanId = await h.claim(agent);
    await withBotApi(fake, async () => {
        assert.deepEqual(await connectTelegram(env, {humanId, authAt: Date.now(), agentId: agent.accountId, botToken: token, chat: '@hscbeats'}), {ok: true, accounts: ['HSC Beats (@hscbeats)']});
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
const sends = (fake: Fake) => fake.calls.filter(call => call.method.startsWith('send')).map(call => call.method);

test('proposeExternalAction_PostOutsideTheFormat_NamesTheField', () => scenario(async ({h, fake, env}) => {
    const {agent} = await connected(h, env, fake);
    const media = (overrides: Record<string, unknown> = {}) => ({mediaId: crypto.randomUUID(), bytes: 10, sha256: 'a'.repeat(64), ...overrides});
    const shape = 'must be JSON {text, media: [{mediaId, bytes, sha256, alt?}], settings?, publishAt?}';
    const pin = 'must name your uploaded file with its exact mediaId, bytes and sha256';
    const cases: [unknown, string][] = [
        ['not an object', shape],
        [[], shape],
        [{text: 'x', media: [], langs: ['en']}, shape + '; no other keys are taken'],
        [{text: 7, media: []}, 'text must be a string'],
        [{media: []}, 'text must be a string'],
        [{text: 'x'}, 'media must be an array of {mediaId, bytes, sha256, alt?}; use [] for none'],
        [{text: 'x', media: {}}, 'media must be an array of {mediaId, bytes, sha256, alt?}; use [] for none'],
        [{text: 'x', media: [], settings: []}, 'settings must be an object'],
        [{text: 'x', media: [], publishAt: 'tomorrow'}, 'publishAt must be a UTC time such as 2026-01-31T09:00:00Z'],
        [{text: 'x', media: [], publishAt: '2030-01-31T09:00:00+10:00'}, 'publishAt must be a UTC time such as 2026-01-31T09:00:00Z'],
        [{text: 'x', media: ['id']}, 'media[0] must be {mediaId, bytes, sha256, alt?}'],
        [{text: 'x', media: [media(), media({url: 'https://example.com/a.png'})]}, 'media[1] must be {mediaId, bytes, sha256, alt?}; no other keys are taken'],
        [{text: 'x', media: [media({mediaId: 'nope'})]}, 'media[0] ' + pin],
        [{text: 'x', media: [media({bytes: 0})]}, 'media[0] ' + pin],
        [{text: 'x', media: [media({sha256: 'A'.repeat(64)})]}, 'media[0] ' + pin],
        [{text: 'x', media: [media({alt: 3})]}, 'media[0].alt must be a string'],
        [{text: 'x', media: [media({alt: 'a'.repeat(2001)})]}, 'media[0].alt must be at most 2000 characters']];
    for (const [content, reason] of cases) {
        const response = await propose(h, agent, content);
        assert.equal(response.status, 400, JSON.stringify(content).slice(0, 80));
        const problem = await response.json() as {code: string; field: string; reason: string};
        assert.deepEqual({code: problem.code, field: problem.field, reason: problem.reason}, {code: 'invalid', field: 'content', reason}, JSON.stringify(content).slice(0, 80));
    }
    // The format's own limits still pass parsing and meet the platform's rules: Telegram takes no alt, so 2000 characters of alt is refused by the adapter rule, not the shape.
    const atShapeLimit = await (await propose(h, agent, {text: 'x', media: [media({alt: 'a'.repeat(2000)})]})).json() as {reason: string};
    assert.equal(atShapeLimit.reason, 'alt is not supported here; omit it');
    assert.equal(fake.calls.length, 0, 'nothing reaches Telegram at propose');
}));

test('connectTelegram_AdminBot_StoresChatAndSealedToken', () => scenario(async ({h, fake, env}) => {
    const {agent} = await connected(h, env, fake);
    const row = (await h.db.prepare('SELECT credential_ref,provider,revision,target_account,target_display,secret FROM external_credentials WHERE account_id=?').bind(agent.accountId).first<any>())!;
    assert.deepEqual([row.credential_ref, row.provider, row.target_account, row.target_display], [ref, 'telegram', String(chatId), 'HSC Beats (@hscbeats)']);
    const sealed = row.secret instanceof ArrayBuffer ? new Uint8Array(row.secret) : Uint8Array.from(row.secret);
    assert.ok(!Buffer.from(sealed).toString('latin1').includes(token.split(':')[1]), 'the token is never stored in clear');
    assert.equal(await openSecret(key, {accountId: agent.accountId, credentialRef: ref, provider: 'telegram', revision: row.revision}, sealed), token);
    assert.deepEqual((await h.ok('/v1/external-credentials', agent)).items.map((item: any) => [item.credentialRef, item.operations, item.textRules, item.mediaRules]),
        [[ref, ['publish_post'], {max: 4096, unit: 'characters', maxWithMedia: 1024, maxBytes: null},
            {max: 10, types: {'image/jpeg': 10_000_000, 'image/png': 10_000_000, 'video/mp4': 50_000_000}, maxTotalBytes: 50_000_000, maxAlt: 0}]]);
}));

test('connectTelegram_ValuesPastedWithSurroundingSpaces_ConnectsAndSealsTrimmedToken', () => scenario(async ({h, fake, env}) => {
    // Copying a token often brings a space along; the form keeps it, so the router trims every connect value before checking it.
    const agent = await h.register(), humanId = await h.claim(agent);
    await withBotApi(fake, async () => {
        assert.deepEqual(await connectTelegram(env, {humanId, authAt: Date.now(), agentId: agent.accountId, botToken: '  ' + token + ' ', chat: ' @hscbeats '}),
            {ok: true, accounts: ['HSC Beats (@hscbeats)']});
    });
    assert.ok(fake.calls.every(call => call.tokenInPath), 'only the trimmed token is sent to Telegram');
    const row = (await h.db.prepare('SELECT revision,secret FROM external_credentials WHERE account_id=?').bind(agent.accountId).first<any>())!;
    const sealed = row.secret instanceof ArrayBuffer ? new Uint8Array(row.secret) : Uint8Array.from(row.secret);
    assert.equal(await openSecret(key, {accountId: agent.accountId, credentialRef: ref, provider: 'telegram', revision: row.revision}, sealed), token, 'the trimmed token is sealed');
    const other = await h.register(), otherHuman = await h.claim(other);
    fake.calls.length = 0;
    await withBotApi(fake, async () => {
        assert.deepEqual(await connectTelegram(env, {humanId: otherHuman, authAt: Date.now(), agentId: other.accountId, botToken: '   ', chat: '@hscbeats'}),
            {ok: false, reason: 'not-found'}, 'a value of only spaces is missing, not sent');
    });
    assert.equal(fake.calls.length, 0);
}));

test('connectTelegram_TitleWithControlOrBidiCharacters_StoredDisplayVisibleAndWellFormed', () => scenario(async ({h, fake, env}) => {
    const agent = await h.register(), humanId = await h.claim(agent);
    // A chat title cut inside an emoji, with a newline, a right-to-left override and a zero-width space.
    fake.answers.getChat = () => ok({id: chatId, type: 'channel', title: 'HSC\n\u202eBeats\u200b ' + 'x'.repeat(107) + '\u{1F600}', username: 'hscbeats'});
    await withBotApi(fake, async () => {
        const result = await connectTelegram(env, {humanId, authAt: Date.now(), agentId: agent.accountId, botToken: token, chat: '@hscbeats'});
        assert.deepEqual(result, {ok: true, accounts: ['HSC Beats ' + 'x'.repeat(107) + '\ufffd (@hscbeats)']});
    });
    const display = (await h.db.prepare('SELECT target_display FROM external_credentials WHERE account_id=?').bind(agent.accountId).first<string>('target_display'))!;
    assert.equal(display, 'HSC Beats ' + 'x'.repeat(107) + '\ufffd (@hscbeats)');
    assert.equal((await h.ok('/v1/external-credentials', agent)).items[0].targetDisplay, display);
}));
test('connectTelegram_TitleOnlyInvisibleCharacters_DisplayFallsBackToChatId', () => scenario(async ({h, fake, env}) => {
    const agent = await h.register(), humanId = await h.claim(agent);
    fake.answers.getChat = () => ok({id: chatId, type: 'channel', title: '\u200b\u202e\u2066'});
    await withBotApi(fake, async () => {
        assert.deepEqual(await connectTelegram(env, {humanId, authAt: Date.now(), agentId: agent.accountId, botToken: token, chat: '@hscbeats'}),
            {ok: true, accounts: [String(chatId)]});
    });
}));
test('connectTelegram_TitleOnlyDefaultIgnorableCharacters_DisplayFallsBackToChatId', () => scenario(async ({h, fake, env}) => {
    const agent = await h.register(), humanId = await h.claim(agent);
    // Zero-width non-joiner, function application, a variation selector, a tag character and the Hangul filler.
    fake.answers.getChat = () => ok({id: chatId, type: 'channel', title: '\u200c\u2061\ufe0f\u{E0041}\u3164'});
    await withBotApi(fake, async () => {
        assert.deepEqual(await connectTelegram(env, {humanId, authAt: Date.now(), agentId: agent.accountId, botToken: token, chat: '@hscbeats'}),
            {ok: true, accounts: [String(chatId)]});
    });
}));
test('connectTelegram_NotAnAdminPosterOrBadInput_NothingStored', () => scenario(async ({h, fake, env}) => {
    const agent = await h.register(), humanId = await h.claim(agent);
    const attempt = (overrides: Record<string, unknown> = {}) => connectTelegram(env, {humanId, authAt: Date.now(), agentId: agent.accountId, botToken: token, chat: '@hscbeats', ...overrides} as any);
    await withBotApi(fake, async () => {
        for (const [name, input] of [['not a token', {botToken: 'my-password'}], ['not a chat', {chat: 'https://t.me/x'}]] as const)
            assert.deepEqual(await attempt(input), {ok: false, reason: 'provider-refused'}, name);
        assert.equal(fake.calls.length, 0, 'malformed inputs are never sent to Telegram');
        const cases: [string, Record<string, (call: Call) => Response>][] = [
            ['wrong token', {getMe: () => refused(401, 'Unauthorized')}],
            ['a user account', {getMe: () => ok({id: botId, is_bot: false})}],
            ['a private chat', {getChat: () => ok({id: 42, type: 'private', first_name: 'x'})}],
            ['a plain member', {getChatMember: () => ok({status: 'member'})}],
            ['an admin who cannot post', {getChatMember: () => ok({status: 'administrator', can_post_messages: false})}],
        ];
        for (const [name, answers] of cases) {
            fake.answers = answers;
            assert.deepEqual(await attempt(), {ok: false, reason: 'provider-refused'}, name);
        }
        fake.answers = {};
        assert.deepEqual(await attempt({authAt: Date.now() - 11 * 60_000}), {ok: false, reason: 'stale-authentication'});
        assert.deepEqual(await attempt({humanId: await h.claim(await h.register())}), {ok: false, reason: 'not-found'});
    });
    // A group's administrator needs no posting right of its own.
    fake.answers = {getChat: () => ok({id: chatId, type: 'supergroup', title: 'Fans'}), getChatMember: () => ok({status: 'administrator'})};
    await withBotApi(fake, async () => assert.equal((await attempt()).ok, true));
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_credentials').first<number>('n'), 1);
}));

test('telegramRules_Limits_TextCaptionAlbumTotalSizeAndNoSchedule', async () => {
    // Every refusal here comes before any stored file is read, so no transaction is needed.
    const check = (post: unknown) => publishingProvider(telegram).checkContent!(undefined as never, {accountId: '', operation: 'publish_post', content: JSON.stringify(post), targetAccount: String(chatId)});
    const file = (bytes = 1000) => ({mediaId: crypto.randomUUID(), bytes, sha256: 'a'.repeat(64)});
    assert.equal(await check({text: 'x'.repeat(4096), media: []}), null);
    const cases: [unknown, RegExp][] = [[{text: 'x'.repeat(4097), media: []}, /at most 4096 characters/], [{text: 'x'.repeat(1025), media: [file()]}, /at most 1024 characters with media/],
        [{text: '', media: []}, /text or media is required/], [{text: 'x', media: Array.from({length: 11}, () => file())}, /at most 10 media/],
        [{text: 'x', media: [file(30_000_000), file(30_000_000)]}, /50000000 bytes in all/], [{text: 'x', media: [], parse_mode: 'HTML'}, /must be JSON/],
        [{text: 'x', media: [], publishAt: new Date(Date.now() + 3600_000).toISOString().replace(/\.\d{3}Z$/, 'Z')}, /publishAt is not supported/],
        [{text: 'x', media: [], settings: {silent: true}}, /settings are not taken/]];
    for (const [post, reason] of cases) assert.match((await check(post)) ?? '', reason, JSON.stringify(post).slice(0, 70));
});

test('executeTelegramPost_Text_SentOnceAsPlainText', () => scenario(async ({h, fake, env}) => {
    const {agent, actionId} = await approvedPost(h, env, fake, {text: 'New drop <b>tonight</b> *8pm*', media: []});
    const first = await execute(h, agent, actionId), again = await execute(h, agent, actionId);
    assert.deepEqual([first.status, first.receipt, again.receipt], ['completed', chatId + '/40', chatId + '/40']);
    assert.deepEqual(sends(fake), ['sendMessage']);
    assert.deepEqual(fake.calls[0].json, {chat_id: String(chatId), text: 'New drop <b>tonight</b> *8pm*'}, 'no parse_mode: the approved text is sent literally');
}));

test('executeTelegramPost_PhotoVideoAndAlbum_UploadExactFiles', () => scenario(async ({h, fake, env}) => {
    const png = randomBytes(4000), mp4 = randomBytes(6000);
    let pinned: {mediaId: string; bytes: number; sha256: string}[] = [];
    const {agent, actionId} = await approvedPost(h, env, fake, async (agent: any) => {
        pinned = [await upload(h, agent, png, 'image/png'), await upload(h, agent, mp4, 'video/mp4')];
        return {text: 'Cover and teaser', media: pinned};
    });
    const result = await execute(h, agent, actionId);
    assert.deepEqual([result.status, result.receipt], ['completed', chatId + '/40']);
    assert.deepEqual(sends(fake), ['sendMediaGroup']);
    const form = fake.calls[0].form!;
    assert.deepEqual(JSON.parse(String(form.get('media'))), [{type: 'photo', media: 'attach://file0', caption: 'Cover and teaser'}, {type: 'video', media: 'attach://file1'}]);
    assert.ok((form.get('file0') as any).bytes.equals(png) && (form.get('file1') as any).bytes.equals(mp4), 'the exact uploaded files');
    // A single file goes out as that kind of message with its caption.
    const single = await (await propose(h, agent, {text: 'Teaser', media: [pinned[1]]})).json() as {actionId: string};
    const humanId = (await h.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(agent.accountId).first<string>('human_id'))!;
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    await ownerAction(env, {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: single.actionId, planDigest: overview.externalActions.find(a => a.actionId === single.actionId)!.planDigest} as OwnerAction);
    assert.equal((await execute(h, agent, single.actionId)).status, 'completed');
    const video = fake.calls.at(-1)!;
    assert.deepEqual([video.method, video.form!.get('caption'), (video.form!.get('video') as any).bytes.equals(mp4)], ['sendVideo', 'Teaser', true]);
}));

test('proposeTelegramPost_UnsupportedOrUnpinnedMedia_Refused', () => scenario(async ({h, fake, env}) => {
    const {agent} = await connected(h, env, fake);
    const webp = await upload(h, agent, randomBytes(100), 'image/webp'), png = await upload(h, agent, randomBytes(100), 'image/png');
    for (const media of [[webp], [{...png, bytes: png.bytes + 1}]]) assert.equal((await propose(h, agent, {text: 'x', media})).status, 400);
}));

test('executeTelegramPost_RefusedOrUncertain_FailedOrUnknownAndNeverResent', () => scenario(async ({h, fake, env}) => {
    for (const [status, expected] of [[400, 'failed'], [403, 'failed'], [429, 'failed'], [502, 'unknown']] as const) {
        fake.answers = {sendMessage: () => status === 502 ? new Response('bad gateway', {status}) : refused(status)};
        const {agent, actionId} = await approvedPost(h, env, fake, {text: 'status ' + status, media: []});
        const result = await execute(h, agent, actionId);
        assert.equal(result.status, expected, String(status));
        if (expected === 'failed') assert.equal(result.failure, 'provider-rejected: telegram-refused-' + status);
        fake.answers = {};
        await execute(h, agent, actionId);
        assert.equal(sends(fake).length, 1, 'never sent again: ' + status);
        fake.calls.length = 0;
    }
}));
