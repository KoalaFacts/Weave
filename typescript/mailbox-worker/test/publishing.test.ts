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
        let sent = 0;
        const provider = publishingProvider({...adapter, publish: async () => { sent++; return {status: 'completed', receipt: 'r'}; }});
        const result = await provider.execute(undefined as never, {accountId: 'a', actionId: crypto.randomUUID(), operation: 'publish_post',
            content: JSON.stringify(post), targetAccount: target, secret: 'token'});
        assert.deepEqual([result, sent], [{status: 'rejected', reason: 'plan-unreadable'}, 0], adapter.name);
    }
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
