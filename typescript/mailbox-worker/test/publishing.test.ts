import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.ts';
import { describePost, parsePost, publishingProvider, type PublishCall, type PublishingAdapter } from '../src/publishing.ts';
import { postiz } from '../src/postiz-provider.ts';
import { telegram } from '../src/telegram-provider.ts';
import type { Env } from '../src/database.ts';

const file = (overrides: Record<string, unknown> = {}) => ({mediaId: crypto.randomUUID(), bytes: 10, sha256: 'a'.repeat(64), ...overrides});
const future = (ms: number) => new Date(Date.now() + ms).toISOString().replace(/\.\d{3}Z$/, 'Z');
const check = (adapter: PublishingAdapter, post: unknown, targetAccount = 'x/1') =>
    publishingProvider(adapter).checkContent!(undefined as never, {accountId: '', operation: 'publish_post', content: JSON.stringify(post), targetAccount});

test('parsePost_OneFormat_OptionalAltSettingsAndSchedule', () => {
    assert.deepEqual(parsePost(JSON.stringify({text: 'hi', media: []})), {text: 'hi', media: [], settings: {}, publishAt: null});
    const media = file({alt: 'A cover'});
    assert.deepEqual(parsePost(JSON.stringify({text: '', media: [media], settings: {a: 1}, publishAt: '2030-01-01T00:00:00Z'}))!.media, [media]);
    assert.equal(parsePost(JSON.stringify({text: 'x', media: [file()]}))!.media[0].alt, null, 'alt is optional');
    for (const post of [{text: 'x'}, {text: 'x', media: [], langs: ['en']}, {text: 'x', media: [file({extra: 1})]}, {text: 'x', media: [file({bytes: 0})]},
        {text: 'x', media: [file({alt: 'x'.repeat(2001)})]}, {text: 'x', media: [], settings: []}, {text: 'x', media: [], publishAt: 'tomorrow'}, {text: 1, media: []}])
        assert.equal(parsePost(JSON.stringify(post)), null, JSON.stringify(post).slice(0, 60));
    assert.equal(parsePost('not json'), null);
});

test('altRules_AdaptersThatSendNoAlt_RefuseAltBeforeApproval', async () => {
    // Telegram and Postiz request bodies carry no alt text, so accepting it would let an owner approve a description that is never sent.
    const linkedin = 'linkedin-page/cm4', settings = {__type: 'linkedin-page'};
    for (const [adapter, post, target] of [[telegram, {text: 'x', media: [file({alt: 'A cover'})]}, '-100123'],
        [postiz, {text: 'x', media: [file({alt: 'A cover'})], settings}, linkedin]] as const) {
        assert.equal(adapter.rules.media.maxAlt, 0, adapter.name);
        assert.match((await check(adapter, post, target)) ?? '', /alt is not supported here; omit it/, adapter.name);
        assert.ok(publishingProvider(adapter).describeContent(target).includes('takes none'), adapter.name + ' tells agents alt is not taken');
    }
});

test('execute_AltApprovedBeforeAltWasRefused_RejectedWithoutSending', async () => {
    // contractRevision is unchanged because what is sent is unchanged; a plan with alt approved before this rule is refused at execute, never sent without its alt.
    const linkedin = 'linkedin-page/cm4', settings = {__type: 'linkedin-page'};
    for (const [adapter, post, target] of [[telegram, {text: 'x', media: [file({alt: 'A cover'})]}, '-100123'],
        [postiz, {text: 'x', media: [file({alt: 'A cover'})], settings}, linkedin]] as const) {
        let sent = 0, envReads = 0;
        // The refusal must come before any storage read or network call: the env records any use, publish counts sends.
        const env = new Proxy({}, {get: () => { envReads++; return undefined; }}) as Env;
        const provider = publishingProvider({...adapter, publish: async () => { sent++; return {status: 'completed', receipt: 'r'}; }});
        const result = await provider.execute(env, {accountId: 'a', actionId: crypto.randomUUID(), operation: 'publish_post',
            content: JSON.stringify(post), targetAccount: target, secret: 'token'}).catch((error: unknown) => ({thrown: String(error)}));
        assert.deepEqual([result, envReads, sent], [{status: 'rejected', reason: 'plan-unreadable'}, 0, 0], adapter.name);
    }
});

test('postizTextRules_ChannelWithAStricterPlatform_RefusedAtProposeAndListed', async () => {
    // Postiz takes 7000 characters, but the platform behind a channel may refuse less after the owner approved it; the smaller limit is enforced at propose.
    const family = '\u{1F468}\u200D\u{1F469}\u200D\u{1F467}\u200D\u{1F466}'; // one grapheme, 25 UTF-8 bytes
    const cases: [string, Record<string, unknown>, string, string, RegExp, object][] = [
        ['x/cm4', {__type: 'x', who_can_reply_post: 'everyone'}, 'a'.repeat(280), '\u6f22'.repeat(141), /at most 280 weighted characters/,
            {max: 280, unit: 'weighted characters', maxWithMedia: null, maxBytes: null}],
        ['bluesky/cm4', {__type: 'bluesky'}, 'a'.repeat(300), 'a'.repeat(301), /at most 300 graphemes/, {max: 300, unit: 'graphemes', maxWithMedia: null, maxBytes: 3000}],
        ['bluesky/cm4', {__type: 'bluesky'}, family.repeat(120), family.repeat(121), /at most 3000 bytes/, {max: 300, unit: 'graphemes', maxWithMedia: null, maxBytes: 3000}],
        ['linkedin/cm4', {__type: 'linkedin'}, 'a'.repeat(3000), 'a'.repeat(3001), /at most 3000 characters/, {max: 3000, unit: 'characters', maxWithMedia: null, maxBytes: null}],
        ['linkedin-page/cm4', {__type: 'linkedin-page'}, 'a'.repeat(3000), 'a'.repeat(3001), /at most 3000 characters/, {max: 3000, unit: 'characters', maxWithMedia: null, maxBytes: null}],
        ['instagram/cm4', {__type: 'instagram', post_type: 'post'}, 'a'.repeat(2200), 'a'.repeat(2201), /at most 2200 characters/, {max: 2200, unit: 'characters', maxWithMedia: null, maxBytes: null}],
        ['instagram-standalone/cm4', {__type: 'instagram-standalone', post_type: 'post'}, 'a'.repeat(2200), 'a'.repeat(2201), /at most 2200 characters/,
            {max: 2200, unit: 'characters', maxWithMedia: null, maxBytes: null}],
        // Channels without a stricter platform limit Weave can stand behind keep Postiz's own.
        ['threads/cm4', {__type: 'threads'}, 'a'.repeat(7000), 'a'.repeat(7001), /at most 7000 characters/, {max: 7000, unit: 'characters', maxWithMedia: null, maxBytes: null}],
        ['mastodon/cm4', {__type: 'mastodon'}, 'a'.repeat(7000), 'a'.repeat(7001), /at most 7000 characters/, {max: 7000, unit: 'characters', maxWithMedia: null, maxBytes: null}]];
    const provider = publishingProvider(postiz);
    for (const [target, settings, fits, over, refusal, listed] of cases) {
        assert.equal(await check(postiz, {text: fits, media: [], settings}, target), null, target + ' accepts text at its limit');
        assert.match((await check(postiz, {text: over, media: [], settings}, target)) ?? '', refusal, target);
        assert.deepEqual(provider.textRules?.(target), listed, target + ' lists the limit it enforces');
        const max = (listed as {max: number}).max, unit = (listed as {unit: string}).unit;
        assert.ok(provider.describeContent(target).includes(`"text": at most ${max} ${unit}`), target + ' tells agents the same limit');
    }
    assert.equal(provider.textRules?.('constructor/cm4').max, 7000, 'only the listed channel types, never inherited names');
});

test('execute_PostizTextApprovedBeforeThePlatformLimit_RejectedWithoutSending', async () => {
    // contractRevision is unchanged: a plan within the new limit sends exactly what was approved; a longer one is refused before any read or send.
    let sent = 0, envReads = 0;
    const env = new Proxy({}, {get: () => { envReads++; return undefined; }}) as Env;
    const provider = publishingProvider({...postiz, publish: async () => { sent++; return {status: 'completed', receipt: 'r'}; }});
    const result = await provider.execute(env, {accountId: 'a', actionId: crypto.randomUUID(), operation: 'publish_post',
        content: JSON.stringify({text: 'a'.repeat(281), media: [], settings: {__type: 'x', who_can_reply_post: 'everyone'}}), targetAccount: 'x/cm4', secret: 'token'})
        .catch((error: unknown) => ({thrown: String(error)}));
    assert.deepEqual([result, envReads, sent], [{status: 'rejected', reason: 'plan-unreadable'}, 0, 0]);
});

test('postizSettingRules_XReplySetting_OnlyDocumentedValuesAccepted', async () => {
    // A value Postiz does not take would be approved and then fail at Postiz; it is refused at propose instead.
    const target = 'x/cm4', provider = publishingProvider(postiz);
    const post = (who_can_reply_post: unknown) => ({text: 'x', media: [], settings: {__type: 'x', who_can_reply_post}});
    for (const value of ['everyone', 'following', 'mentionedUsers', 'subscribers', 'verified']) assert.equal(await check(postiz, post(value), target), null, value);
    for (const value of ['Everyone', 'anyone', '', ' everyone']) assert.match((await check(postiz, post(value), target)) ?? '', /settings\.who_can_reply_post must be one of "everyone", /, value);
    for (const value of [true, 1, null, ['everyone']]) assert.match((await check(postiz, post(value), target)) ?? '', /settings\.who_can_reply_post must be a string/, JSON.stringify(value));
    assert.deepEqual(provider.settingRules?.(target), {who_can_reply_post: {type: 'string', values: ['everyone', 'following', 'mentionedUsers', 'subscribers', 'verified']}});
    // Channels without a documented list keep presence-only checks; inherited names never match.
    assert.equal(await check(postiz, {text: 'x', media: [], settings: {__type: 'pinterest', board: 'anything'}}, 'pinterest/cm4'), null);
    assert.deepEqual([provider.settingRules?.('pinterest/cm4'), provider.settingRules?.('constructor/cm4')], [{}, {}]);
});

test('postizSettings_RequiredSettingBlankOrNull_RefusedBeforeApproval', async () => {
    // A blank board, subreddit, channel or YouTube title is present but names nothing; Postiz refuses it after the owner approved it.
    for (const [target, settings, name] of [['pinterest/cm4', {__type: 'pinterest'}, 'board'], ['reddit/cm4', {__type: 'reddit'}, 'subreddit'],
        ['discord/cm4', {__type: 'discord'}, 'channel'], ['youtube/cm4', {__type: 'youtube', type: 'public'}, 'title']] as const) {
        // A typed setting (the YouTube title) refuses null as the wrong type first.
        for (const value of name === 'title' ? ['', '   ', '\t\n'] : ['', '   ', '\t\n', null]) {
            const post = {text: 'x', media: [], settings: {...settings, [name]: value}};
            assert.match((await check(postiz, post, target)) ?? '', new RegExp(`settings\\.${name} must not be blank`), `${target} ${JSON.stringify(value)}`);
        }
        assert.equal(await check(postiz, {text: 'x', media: [], settings: {...settings, [name]: ' a '}}, target), null, target);
    }
    // Structured values Postiz documents for some channels (Reddit's subreddit list) are still checked for presence only.
    for (const subreddit of [[{value: {subreddit: 'a'}}], []])
        assert.equal(await check(postiz, {text: 'x', media: [], settings: {__type: 'reddit', subreddit}}, 'reddit/cm4'), null, JSON.stringify(subreddit));
    // Only required settings are refused when blank; an optional one is passed on as sent.
    assert.equal(await check(postiz, {text: 'x', media: [], settings: {__type: 'pinterest', board: 'b', title: ' '}}, 'pinterest/cm4'), null);
});

test('execute_PostizBlankSettingApprovedBeforeItWasRefused_RejectedWithoutSending', async () => {
    let sent = 0, envReads = 0;
    const env = new Proxy({}, {get: () => { envReads++; return undefined; }}) as Env;
    const provider = publishingProvider({...postiz, publish: async () => { sent++; return {status: 'completed', receipt: 'r'}; }});
    const result = await provider.execute(env, {accountId: 'a', actionId: crypto.randomUUID(), operation: 'publish_post',
        content: JSON.stringify({text: 'x', media: [], settings: {__type: 'pinterest', board: ' '}}), targetAccount: 'pinterest/cm4', secret: 'token'})
        .catch((error: unknown) => ({thrown: String(error)}));
    assert.deepEqual([result, envReads, sent], [{status: 'rejected', reason: 'plan-unreadable'}, 0, 0]);
});

test('execute_PostizSettingApprovedBeforeValuesWereChecked_RejectedWithoutSending', async () => {
    let sent = 0, envReads = 0;
    const env = new Proxy({}, {get: () => { envReads++; return undefined; }}) as Env;
    const provider = publishingProvider({...postiz, publish: async () => { sent++; return {status: 'completed', receipt: 'r'}; }});
    const result = await provider.execute(env, {accountId: 'a', actionId: crypto.randomUUID(), operation: 'publish_post',
        content: JSON.stringify({text: 'x', media: [], settings: {__type: 'x', who_can_reply_post: 'anyone'}}), targetAccount: 'x/cm4', secret: 'token'})
        .catch((error: unknown) => ({thrown: String(error)}));
    assert.deepEqual([result, envReads, sent], [{status: 'rejected', reason: 'plan-unreadable'}, 0, 0]);
});

test('postizRules_SettingsTypeRequiredNamesAndScheduleHorizon', async () => {
    const tiktok = 'tiktok/cm4';
    assert.match((await check(postiz, {text: 'x', media: []}, tiktok))!, /settings\.__type must be "tiktok"/);
    assert.match((await check(postiz, {text: 'x', media: [], settings: {__type: 'tiktok'}}, tiktok))!, /settings must include privacy_level/);
    const settings = {__type: 'linkedin-page'};
    assert.equal(await check(postiz, {text: 'x', media: [], settings, publishAt: future(3600_000)}, 'linkedin-page/cm4'), null);
    assert.match((await check(postiz, {text: 'x', media: [], settings, publishAt: future(-60_000)}, 'linkedin-page/cm4'))!, /future UTC time at most 90 days/);
    assert.match((await check(postiz, {text: 'x', media: [], settings, publishAt: future(91 * 86_400_000)}, 'linkedin-page/cm4'))!, /at most 90 days/);
    assert.match((await check(postiz, {text: '', media: [], settings}, 'linkedin-page/cm4'))!, /text or media is required/);
    const described = describePost(postiz.rules, tiktok);
    assert.ok(described.includes('"__type": "tiktok"') && described.includes('"privacy_level"') && described.includes('"publishAt"'), described);
});

/** A test-only platform: everything the router needs from a platform, and nothing else. */
const demo = (sent: PublishCall[]): PublishingAdapter => ({
    name: 'demo-platform', contractRevision: 'demo-v1',
    rules: {text: {max: 20, unit: 'characters', maxWithMedia: 5}, media: {max: 2, types: {'image/png': 100}, maxTotalBytes: 150}, schedule: null},
    connector: {label: 'Demo', replaces: false, fields: [{name: 'key', label: 'Key', secret: true}], verify: async () => []},
    publish: async call => { sent.push(call); return {status: 'completed', receipt: 'demo:' + call.actionId}; }
});

test('publishingProvider_AnyAdapter_ValidatesThenHandsItTheApprovedPostAndStoredFiles', async () => {
    const h = await start();
    try {
        const sent: PublishCall[] = [], adapter = demo(sent), provider = publishingProvider(adapter);
        const agent = await h.register(), media = file({bytes: 80}), at = (BigInt(Date.now()) * 10000n).toString();
        await h.db.prepare(`INSERT INTO external_media(media_id,account_id,content_type,bytes,sha256,created_at) VALUES(?,?,'image/png',80,?,CAST(? AS INTEGER))`)
            .bind(media.mediaId, agent.accountId, media.sha256, at).run();
        assert.deepEqual([provider.name, provider.contractRevision, provider.operations], ['demo-platform', 'demo-v1', ['publish_post']]);
        assert.match((await check(adapter, {text: 'x'.repeat(6), media: [media]}))!, /at most 5 characters with media/, 'the caption limit applies with media');
        assert.match((await check(adapter, {text: 'x', media: [media, media]}))!, /150 bytes in all/);
        assert.ok(provider.describeContent('x').includes('image/png up to 100 bytes'));
        const env = {db: h.db, EXTERNAL_CREDENTIAL_KEY: 'k'} as unknown as Env;
        const content = JSON.stringify({text: 'Cover', media: [{...media, alt: 'Album cover'}]});
        const result = await provider.execute(env, {accountId: agent.accountId, operation: 'publish_post', content, targetAccount: 'acct', actionId: 'a1', secret: 's3cret'});
        assert.deepEqual(result, {status: 'completed', receipt: 'demo:a1'});
        assert.deepEqual(sent.map(call => [call.targetAccount, call.secret, call.post]), [['acct', 's3cret',
            {text: 'Cover', media: [{...media, alt: 'Album cover', contentType: 'image/png'}], settings: {}, publishAt: null}]]);
        // At dispatch the router re-reads the files: a file gone or changed since approval sends nothing.
        await h.db.prepare('DELETE FROM external_media WHERE media_id=?').bind(media.mediaId).run();
        assert.deepEqual(await provider.execute(env, {accountId: agent.accountId, operation: 'publish_post', content, targetAccount: 'acct', actionId: 'a2', secret: 's'}),
            {status: 'rejected', reason: 'media-unavailable'});
        assert.equal(sent.length, 1);
    } finally { await h.close(); }
});

test('postizSettingRules_InstagramYoutubeTiktok_DocumentedTypesAndValuesChecked', async () => {
    // Values from docs.postiz.com/public-api/providers/{instagram,youtube,tiktok}; anything else fails at Postiz after approval.
    const provider = publishingProvider(postiz);
    const tiktokSettings = {__type: 'tiktok', privacy_level: 'SELF_ONLY', duet: false, stitch: false, comment: true, autoAddMusic: 'no',
        brand_content_toggle: false, brand_organic_toggle: false, content_posting_method: 'DIRECT_POST'};
    const post = (settings: Record<string, unknown>) => ({text: 'x', media: [], settings});
    for (const [target, settings] of [['instagram/cm4', {__type: 'instagram', post_type: 'story'}], ['instagram-standalone/cm4', {__type: 'instagram-standalone', post_type: 'post'}],
        ['youtube/cm4', {__type: 'youtube', title: 'My video', type: 'unlisted'}], ['tiktok/cm4', tiktokSettings]] as const)
        assert.equal(await check(postiz, post(settings), target), null, target);
    const refused: [string, Record<string, unknown>, RegExp][] = [
        ['instagram/cm4', {__type: 'instagram', post_type: 'reel'}, /settings\.post_type must be one of "post", "story"/],
        ['youtube/cm4', {__type: 'youtube', title: 'My video', type: 'Public'}, /settings\.type must be one of "public", "unlisted", "private"/],
        ['youtube/cm4', {__type: 'youtube', title: 7, type: 'public'}, /settings\.title must be a string/],
        ['tiktok/cm4', {...tiktokSettings, duet: 'false'}, /settings\.duet must be a boolean/],
        ['tiktok/cm4', {...tiktokSettings, brand_organic_toggle: 0}, /settings\.brand_organic_toggle must be a boolean/],
        ['tiktok/cm4', {...tiktokSettings, privacy_level: 'PUBLIC'}, /settings\.privacy_level must be one of "PUBLIC_TO_EVERYONE", /],
        ['tiktok/cm4', {...tiktokSettings, autoAddMusic: true}, /settings\.autoAddMusic must be a string/],
        ['tiktok/cm4', {...tiktokSettings, content_posting_method: 'DRAFT'}, /settings\.content_posting_method must be one of "DIRECT_POST", "UPLOAD"/]];
    for (const [target, settings, reason] of refused) assert.match((await check(postiz, post(settings), target)) ?? '', reason, JSON.stringify(settings));
    const tiktok = provider.settingRules?.('tiktok/cm4');
    assert.deepEqual(tiktok?.privacy_level, {type: 'string', values: ['PUBLIC_TO_EVERYONE', 'MUTUAL_FOLLOW_FRIENDS', 'FOLLOWER_OF_CREATOR', 'SELF_ONLY']});
    for (const name of ['duet', 'stitch', 'comment', 'brand_content_toggle', 'brand_organic_toggle']) assert.deepEqual(tiktok?.[name], {type: 'boolean', values: null}, name);
    assert.deepEqual(provider.settingRules?.('youtube/cm4'), {title: {type: 'string', values: null}, type: {type: 'string', values: ['public', 'unlisted', 'private']}});
    assert.deepEqual(provider.settingRules?.('instagram-standalone/cm4'), {post_type: {type: 'string', values: ['post', 'story']}});
    // The description says which settings take a boolean.
    assert.match(provider.describeContent('tiktok/cm4'), /"duet": true \| false/);
});
