import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { mediaRetentionMs, sweepMedia } from '../src/external-media.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const day = 24 * 3600_000;
const ticks = (ms: number) => (BigInt(ms) * 10000n).toString();

async function scenario(run: (h: Harness) => Promise<void>) {
    const h = await start();
    try { await run(h); } finally { await h.close(); }
}
async function upload(h: Harness, agent: any) {
    const file = randomBytes(256);
    const response = await h.fetchAs(agent, 'http://localhost/v1/external-media', {method: 'PUT', headers: {'Content-Type': 'image/png', 'Content-Length': String(file.length)}, body: file});
    assert.equal(response.status, 200, await response.clone().text());
    return (await response.json() as {mediaId: string}).mediaId;
}
const age = (h: Harness, mediaId: string, ms: number) => h.db.prepare('UPDATE external_media SET created_at=CAST(? AS INTEGER) WHERE media_id=?').bind(ticks(Date.now() - ms), mediaId).run();
/** A pending action whose content names `mediaId`, last changed `ago` milliseconds ago. */
async function naming(h: Harness, accountId: string, mediaId: string, ago: number) {
    const at = Date.now() - ago, actionId = crypto.randomUUID();
    await h.db.prepare(`INSERT INTO external_actions(action_id,account_id,provider,contract_revision,operation,content,credential_ref,credential_revision,target_account,plan_digest,status,created_at,expires_at,updated_at)
        VALUES(?,?,'postiz','postiz-post-v1','publish_post',?,'postiz:x','rev','x/x','digest','pending',CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER))`)
        .bind(actionId, accountId, JSON.stringify({text: 'x', media: [{mediaId, bytes: 256, sha256: 'a'.repeat(64)}], settings: {__type: 'x'}}), ticks(at), ticks(at + 3600_000), ticks(at)).run();
    return actionId;
}
const cron = async (h: Harness) => assert.equal((await (await h.mf.getWorker()).scheduled({cron: '*/5 * * * *', scheduledTime: Date.now()})).outcome, 'ok');
const stored = async (h: Harness, accountId: string, mediaId: string) => ({
    record: await h.db.prepare('SELECT count(*) n FROM external_media WHERE media_id=?').bind(mediaId).first<number>('n'),
    object: (await (await h.mf.getR2Bucket('EXTERNAL_MEDIA')).head(accountId + '/' + mediaId)) !== null});

test('sweepMedia_OldAndUnused_RecordAndObjectDeleted', () => scenario(async h => {
    const agent = await h.register();
    const old = await upload(h, agent), fresh = await upload(h, agent);
    await age(h, old, mediaRetentionMs + day);
    await age(h, fresh, mediaRetentionMs - day);
    await cron(h);
    assert.deepEqual(await stored(h, agent.accountId, old), {record: 0, object: false});
    assert.deepEqual(await stored(h, agent.accountId, fresh), {record: 1, object: true}, 'media inside the retention period is kept');
    assert.equal((await h.request(`/v1/external-media/${old}`, agent)).status, 404);
}));

test('sweepMedia_NamedByARecentAction_KeptUntilThatActionIsOld', () => scenario(async h => {
    const agent = await h.register(), other = await h.register();
    const mediaId = await upload(h, agent);
    await age(h, mediaId, mediaRetentionMs + 30 * day);
    const actionId = await naming(h, agent.accountId, mediaId, day);
    // Another agent's action naming the same ID does not keep this agent's file.
    await naming(h, other.accountId, mediaId, 0);
    await cron(h);
    assert.deepEqual(await stored(h, agent.accountId, mediaId), {record: 1, object: true});
    await h.db.prepare('UPDATE external_actions SET updated_at=CAST(? AS INTEGER) WHERE action_id=?').bind(ticks(Date.now() - mediaRetentionMs - day), actionId).run();
    await cron(h);
    assert.deepEqual(await stored(h, agent.accountId, mediaId), {record: 0, object: false});
}));

test('sweepMedia_ObjectDeleteFails_RecordStillRemovedAndSweepContinues', () => scenario(async h => {
    const agent = await h.register();
    const first = await upload(h, agent), second = await upload(h, agent);
    for (const id of [first, second]) await age(h, id, mediaRetentionMs + day);
    const removed: string[] = [];
    const env = {db: h.db, EXTERNAL_MEDIA: {delete: async (key: string) => { removed.push(key); if (removed.length === 1) throw Error('R2 unavailable'); }}} as unknown as Env;
    await sweepMedia(env);
    assert.equal(removed.length, 2, 'one failed object does not stop the sweep');
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_media').first<number>('n'), 0);
}));

test('sweepMedia_Maintenance_DeletesNothing', () => scenario(async h => {
    const agent = await h.register();
    const mediaId = await upload(h, agent);
    await age(h, mediaId, mediaRetentionMs + day);
    const env = {db: h.db, MAINTENANCE_MODE: 'true', EXTERNAL_MEDIA: {delete: async () => assert.fail('nothing is deleted in maintenance')}} as unknown as Env;
    await sweepMedia(env);
    assert.deepEqual(await stored(h, agent.accountId, mediaId), {record: 1, object: true});
}));

/** Stores `count` old media records with their objects directly, faster than uploading each. */
async function oldMedia(h: Harness, accountId: string, count: number) {
    const bucket = await h.mf.getR2Bucket('EXTERNAL_MEDIA'), ids: string[] = [];
    for (let i = 0; i < count; i++) {
        const mediaId = crypto.randomUUID();
        await bucket.put(accountId + '/' + mediaId, new Uint8Array([i]));
        await h.db.prepare(`INSERT INTO external_media(media_id,account_id,content_type,bytes,sha256,created_at) VALUES(?,?,'image/png',1,?,CAST(? AS INTEGER))`)
            .bind(mediaId, accountId, 'a'.repeat(64), ticks(Date.now() - mediaRetentionMs - day - i)).run();
        ids.push(mediaId);
    }
    return ids;
}

test('sweepMedia_ManyEligibleFiles_EachCronRunStaysWithinItsDatabaseBudget', () => scenario(async h => {
    const agent = await h.register();
    await oldMedia(h, agent.accountId, 60);
    await cron(h);
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_media').first<number>('n'), 10, 'one run deletes a full batch without exceeding the budget');
    await cron(h);
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_media').first<number>('n'), 0);
    assert.equal((await (await h.mf.getR2Bucket('EXTERNAL_MEDIA')).list()).objects.length, 0);
}));

test('sweepMedia_ReferenceWrittenWithJsonEscapes_StillKeepsTheFile', () => scenario(async h => {
    const agent = await h.register();
    const [escaped, topLevel, plain] = await oldMedia(h, agent.accountId, 3);
    const actionId = await naming(h, agent.accountId, escaped, day);
    // The same plan with every hyphen JSON-escaped: it decodes to the same media ID.
    await h.db.prepare('UPDATE external_actions SET content=? WHERE action_id=?')
        .bind(JSON.stringify({text: 'x', media: [{mediaId: escaped, bytes: 1, sha256: 'a'.repeat(64)}], settings: {__type: 'x'}}).replaceAll('-', '\\u002d'), actionId).run();
    const video = await naming(h, agent.accountId, topLevel, day);
    await h.db.prepare('UPDATE external_actions SET content=? WHERE action_id=?')
        .bind(JSON.stringify({title: 't', description: '', privacyStatus: 'private', mediaId: topLevel, bytes: 1, sha256: 'a'.repeat(64)}).replaceAll('-', '\\u002d'), video).run();
    // Content that is not a plan (plain text, other JSON shapes) neither names a file nor stops the sweep.
    for (const content of ['plain text post', '{"media":["not-an-object", 5]}', '[1,2]', '{"media":{"mediaId":"x"}}'])
        await h.db.prepare('UPDATE external_actions SET content=? WHERE action_id=?').bind(content, await naming(h, agent.accountId, crypto.randomUUID(), 0)).run();
    await cron(h);
    assert.deepEqual(await stored(h, agent.accountId, escaped), {record: 1, object: true});
    assert.deepEqual(await stored(h, agent.accountId, topLevel), {record: 1, object: true});
    assert.deepEqual(await stored(h, agent.accountId, plain), {record: 0, object: false});
}));
