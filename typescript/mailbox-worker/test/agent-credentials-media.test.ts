import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { build } from 'esbuild';
import { start, testGrant } from './harness.ts';
import { createMediaUpload, mediaUrlLifetimeMs, signedMediaUrl, uploadUrlLifetimeMs } from '../src/external-media.ts';
import type { Env } from '../src/database.ts';
// Evaluate the bundled production module, as the other private-tool tests do.
const built = await build({entryPoints: ['src/remote-private.ts'], bundle: true, write: false, platform: 'node', format: 'esm', logLevel: 'silent'});
const {executePrivateMcp} = await import('data:text/javascript;base64,' + Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');

type Harness = Awaited<ReturnType<typeof start>>;
const key = randomBytes(32).toString('base64url'), origin = 'http://localhost';
const tiktok = {__type: 'tiktok', privacy_level: 'PUBLIC_TO_EVERYONE', duet: false, stitch: false, comment: true, autoAddMusic: 'no',
    brand_content_toggle: false, brand_organic_toggle: false, content_posting_method: 'DIRECT_POST'};

async function scenario(run: (s: {h: Harness; env: Env}) => Promise<void>) {
    // The Worker verifies what this process signs, so both hold the same key and origin.
    const h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key, EXTERNAL_MEDIA_ORIGIN: origin}});
    const env = {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '10', REGISTRATION_GLOBAL_PER_HOUR: '100',
        CLEANUP_MAX_MESSAGES: '0', CLEANUP_TIME_BUDGET_MS: '0', EXTERNAL_CREDENTIAL_KEY: key, EXTERNAL_MEDIA_ORIGIN: origin,
        EXTERNAL_MEDIA: await h.mf.getR2Bucket('EXTERNAL_MEDIA')} as unknown as Env;
    try { await run({h, env}); } finally { await h.close(); }
}
async function owned(h: Harness) {
    const agent = await h.register();
    await h.claim(agent);
    return {agent, grantId: await testGrant(h.db, agent.accountId)};
}
async function credential(h: Harness, accountId: string, ref: string, provider: string, target: string, revoked = false) {
    const at = (BigInt(Date.now()) * 10000n).toString();
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at,revoked_at)
        VALUES(?,?,?,'rev-1',?,?,CAST(? AS INTEGER),CAST(? AS INTEGER),${revoked ? 'CAST(? AS INTEGER)' : 'NULL'})`)
        .bind(accountId, ref, provider, target, 'Display ' + ref, at, at, ...(revoked ? [at] : [])).run();
}
const mcp = async (env: Env, accountId: string, grantId: string, name: string, args: Record<string, unknown> = {}) => {
    const response = await executePrivateMcp(env, origin, accountId, name, args, grantId);
    return {status: response.status, body: await response.json() as Record<string, any>};
};
const put = (h: Harness, url: string, bytes: Buffer, type: string, length = bytes.length) =>
    h.mf.dispatchFetch(url, {method: 'PUT', headers: {'Content-Type': type, 'Content-Length': String(length)}, body: bytes} as any);
const objects = async (h: Harness) => (await (await h.mf.getR2Bucket('EXTERNAL_MEDIA')).list()).objects.length;

test('listMyCredentials_LiveCredentials_OperationsAndContentFormatPerProvider', () => scenario(async ({h, env}) => {
    const {agent, grantId} = await owned(h), other = await owned(h);
    await credential(h, agent.accountId, 'postiz:cm4tiktok', 'postiz', 'tiktok/cm4tiktok');
    await credential(h, agent.accountId, 'postiz:cm4old', 'postiz', 'x/cm4old', true);
    await credential(h, agent.accountId, 'postiz:cm4x', 'postiz', 'x/cm4x');
    // Google is not configured here, so the YouTube credential cannot be used and says so.
    await credential(h, agent.accountId, 'youtube', 'youtube', 'UC' + 'a'.repeat(22));
    await credential(h, other.agent.accountId, 'postiz:cm4theirs', 'postiz', 'x/cm4theirs');

    const listed = await mcp(env, agent.accountId, grantId, 'list_my_credentials');
    assert.equal(listed.status, 200, JSON.stringify(listed.body));
    const [postiz, postizX, youtube] = listed.body.items;
    assert.deepEqual(listed.body.items.map((item: any) => item.credentialRef), ['postiz:cm4tiktok', 'postiz:cm4x', 'youtube'], 'revoked and other agents\' credentials are not listed');
    assert.deepEqual([postiz.provider, postiz.targetAccount, postiz.targetDisplay, postiz.operations], ['postiz', 'tiktok/cm4tiktok', 'Display postiz:cm4tiktok', ['publish_post']]);
    for (const setting of Object.keys(tiktok)) assert.ok(postiz.content.includes(`"${setting}"`), setting + ' is named in the content format');
    assert.deepEqual(postiz.textRules, {max: 7000, unit: 'characters', maxWithMedia: null, maxBytes: null}, 'the limits ruleBreak enforces, as data');
    const postizBytes = 100 * 1024 * 1024;
    assert.deepEqual(postiz.mediaRules, {max: 10, types: {'image/jpeg': postizBytes, 'image/png': postizBytes, 'image/gif': postizBytes, 'image/webp': postizBytes, 'video/mp4': postizBytes},
        maxTotalBytes: null, maxAlt: 0}, 'the media limits ruleBreak and storedFiles enforce, as data');
    assert.deepEqual(postizX.textRules, {max: 280, unit: 'weighted characters', maxWithMedia: null, maxBytes: null}, 'an X channel lists X\'s limit, not Postiz\'s');
    assert.ok(postizX.content.includes('"text": at most 280 weighted characters'), postizX.content);
    assert.deepEqual(postiz.settingRules.duet, {type: 'boolean', values: null}, 'a TikTok toggle lists its JSON type');
    assert.deepEqual(postiz.settingRules.content_posting_method, {type: 'string', values: ['DIRECT_POST', 'UPLOAD']}, 'a TikTok closed list is listed');
    assert.deepEqual(postizX.settingRules, {who_can_reply_post: {type: 'string', values: ['everyone', 'following', 'mentionedUsers', 'subscribers', 'verified']}},
        'an X channel lists the reply setting\'s closed values');
    assert.ok(postizX.content.includes('"who_can_reply_post": one of "everyone" | "following" | "mentionedUsers" | "subscribers" | "verified"'), postizX.content);
    assert.deepEqual([youtube.operations, youtube.content, youtube.textRules, youtube.mediaRules, youtube.settingRules], [[], null, null, null, null]);
    // The HTTP API returns the same listing for a key-authenticated agent.
    assert.deepEqual(await h.ok('/v1/external-credentials', agent), listed.body);
    assert.equal((await mcp(env, agent.accountId, grantId, 'list_my_credentials', {extra: 1})).status, 400);
}));

test('createMediaUpload_PutThenPropose_PinsTheUploadedFile', () => scenario(async ({h, env}) => {
    const {agent, grantId} = await owned(h);
    await credential(h, agent.accountId, 'postiz:cm4tiktok', 'postiz', 'tiktok/cm4tiktok');
    const file = randomBytes(4096);
    const minted = await mcp(env, agent.accountId, grantId, 'create_media_upload', {contentType: 'video/mp4', bytes: file.length});
    assert.equal(minted.status, 200, JSON.stringify(minted.body));
    assert.ok(minted.body.uploadUrl.startsWith(origin + `/v1/external-media/${minted.body.mediaId}/content?`));
    assert.equal(await objects(h), 0, 'minting stores nothing');

    const uploaded = await put(h, minted.body.uploadUrl, file, 'video/mp4');
    assert.equal(uploaded.status, 200, await uploaded.clone().text());
    const record = await uploaded.json() as Record<string, any>;
    assert.deepEqual([record.mediaId, record.contentType, record.bytes, record.sha256],
        [minted.body.mediaId, 'video/mp4', file.length, createHash('sha256').update(file).digest('hex')]);
    assert.deepEqual((await mcp(env, agent.accountId, grantId, 'get_my_media', {mediaId: record.mediaId})).body, record);
    assert.deepEqual(await h.ok(`/v1/external-media/${record.mediaId}`, agent), record);

    // The URL is single-use: a second PUT neither replaces nor removes the first file.
    const again = await put(h, minted.body.uploadUrl, randomBytes(file.length), 'video/mp4');
    assert.equal(again.status, 409);
    const stored = await (await h.mf.getR2Bucket('EXTERNAL_MEDIA')).get(agent.accountId + '/' + record.mediaId);
    assert.deepEqual(Buffer.from(await stored!.arrayBuffer()), file);

    const content = JSON.stringify({text: 'New beat', media: [{mediaId: record.mediaId, bytes: record.bytes, sha256: record.sha256}], settings: tiktok});
    const proposed = await mcp(env, agent.accountId, grantId, 'propose_external_action', {credentialRef: 'postiz:cm4tiktok', operation: 'publish_post', content});
    assert.equal(proposed.status, 200, JSON.stringify(proposed.body));
    assert.equal(proposed.body.status, 'pending');
}));

test('signedUpload_TamperedExpiredOrMismatched_NothingStored', () => scenario(async ({h, env}) => {
    const {agent, grantId} = await owned(h), other = await owned(h);
    const file = randomBytes(1024);
    const url = (await mcp(env, agent.accountId, grantId, 'create_media_upload', {contentType: 'image/png', bytes: file.length})).body.uploadUrl as string;
    const with_ = (name: string, value: string) => { const changed = new URL(url); changed.searchParams.set(name, value); return changed.href; };
    const expired = (await createMediaUpload(env, agent.accountId, grantId, 'image/png', file.length, Date.now() - uploadUrlLifetimeMs - 1000)).uploadUrl;
    const otherMedia = url.replace(/external-media\/[0-9a-f-]{36}/, 'external-media/' + crypto.randomUUID());
    for (const [name, target, status] of [['larger size', with_('bytes', '2048'), 404], ['other type', with_('type', 'video/mp4'), 404],
        ['other agent', with_('account', other.agent.accountId), 404], ['other grant', with_('grant', other.grantId), 404],
        ['other media', otherMedia, 404], ['expired', expired, 404], ['extra parameter', url + '&x=1', 404]] as const)
        assert.equal((await put(h, target, file, new URL(target).searchParams.get('type')!)).status, status, name);
    assert.equal((await put(h, url, file, 'image/jpeg')).status, 400, 'Content-Type must be the signed type');
    assert.equal((await put(h, url, file.subarray(0, 512), 'image/png')).status, 400, 'Content-Length must be the signed size');
    assert.equal(await objects(h), 0);

    // Revoking the grant after minting refuses the upload before any byte is stored.
    await h.db.prepare('UPDATE client_grants SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=?').bind((BigInt(Date.now()) * 10000n).toString(), grantId).run();
    assert.equal((await put(h, url, file, 'image/png')).status, 401);
    assert.equal(await objects(h), 0);
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_media').first<number>('n'), 0);
    assert.equal((await mcp(env, agent.accountId, grantId, 'create_media_upload', {contentType: 'image/png', bytes: 1})).status, 401);
}));

test('signedMediaUrl_ExpiredAlteredOrExtraParameter_404', () => scenario(async ({h, env}) => {
    // The download URL Postiz fetches serves the bytes only while its signature, agent, media ID and
    // expiry all match, and nothing else is accepted in the query.
    const {agent, grantId} = await owned(h);
    const file = randomBytes(2048);
    const minted = (await mcp(env, agent.accountId, grantId, 'create_media_upload', {contentType: 'image/png', bytes: file.length})).body;
    assert.equal((await put(h, minted.uploadUrl, file, 'image/png')).status, 200);
    const url = await signedMediaUrl(env, agent.accountId, minted.mediaId, Date.now());
    const served = await h.mf.dispatchFetch(url);
    assert.equal(served.status, 200, 'positive control');
    assert.deepEqual([served.headers.get('Content-Type'), served.headers.get('Cache-Control'), served.headers.get('Content-Disposition')],
        ['image/png', 'no-store', 'attachment']);
    assert.ok(Buffer.from(await served.arrayBuffer()).equals(file));

    const with_ = (name: string, value: string) => { const changed = new URL(url); changed.searchParams.set(name, value); return changed.href; };
    const without = (name: string) => { const changed = new URL(url); changed.searchParams.delete(name); return changed.href; };
    const expired = await signedMediaUrl(env, agent.accountId, minted.mediaId, Date.now() - mediaUrlLifetimeMs - 1000);
    const later = String(Number(new URL(url).searchParams.get('expires')) + 60_000);
    const otherMedia = url.replace(/external-media\/[0-9a-f-]{36}/, 'external-media/' + crypto.randomUUID());
    for (const [name, target] of [['expired', expired], ['expiry extended', with_('expires', later)], ['other media', otherMedia],
        ['no signature', without('signature')], ['no account', without('account')], ['extra parameter', url + '&x=1']] as const) {
        const response = await h.mf.dispatchFetch(target);
        assert.equal(response.status, 404, name);
        assert.equal((await response.json() as {code: string}).code, 'unavailable', name);
    }

    // A file no longer recorded is not served, even through a URL that is still valid.
    await h.db.prepare('DELETE FROM external_media WHERE media_id=?').bind(minted.mediaId).run();
    assert.equal((await h.mf.dispatchFetch(url)).status, 404, 'deleted record');
}));

test('createMediaUpload_InvalidArguments_Refused', () => scenario(async ({h, env}) => {
    const {agent, grantId} = await owned(h);
    for (const args of [{contentType: 'text/html', bytes: 10}, {contentType: 'image/png', bytes: 0}, {contentType: 'image/png', bytes: 100 * 1024 * 1024 + 1},
        {contentType: 'image/png'}, {contentType: 'image/png', bytes: 1, mediaId: crypto.randomUUID()}])
        assert.equal((await mcp(env, agent.accountId, grantId, 'create_media_upload', args)).status, 400, JSON.stringify(args));
    assert.equal((await mcp(env, agent.accountId, grantId, 'get_my_media', {mediaId: crypto.randomUUID()})).status, 404);
}));
