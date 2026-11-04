/**
 * Media an owned agent uploads for an external action (for example a video). Bytes live in R2 under a random ID that is
 * written once; D1 records the owner, size, type and SHA-256, which an action's plan binds. The route never overwrites.
 */
import * as schema from './schema.ts';
import { atomic, type Env, type Transaction } from './database.ts';
import { writesRefused } from './humans.ts';
import type { ActionCaller } from './external-actions.ts';
import { fail, invalidField } from './wire.ts';

/** The R2 binding surface this module uses; the runtime supplies the implementation. */
export interface MediaBucket {
    put(key: string, value: ReadableStream, options: {onlyIf: {etagDoesNotMatch: string}; httpMetadata: {contentType: string}}): Promise<{size: number} | null>;
    get(key: string): Promise<{body: ReadableStream; size: number} | null>;
    delete(key: string): Promise<void>;
}
export type MediaView = {mediaId: string; contentType: string; bytes: number; sha256: string; createdAt: string};
export type StoredMedia = {contentType: string; bytes: number; sha256: string};

/** A Worker request body is bounded by the platform; larger uploads need multipart, which this route does not offer. */
export const maxMediaBytes = 100 * 1024 * 1024;
const contentTypes = new Set(['video/mp4', 'video/quicktime', 'video/webm']);
const mediaIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const objectKey = (accountId: string, mediaId: string) => accountId + '/' + mediaId;

async function sha256Of(stream: ReadableStream<Uint8Array>): Promise<string> {
    const digest = new (crypto as unknown as {DigestStream: new (algorithm: string) => WritableStream<Uint8Array> & {digest: Promise<ArrayBuffer>}}).DigestStream('SHA-256');
    await stream.pipeTo(digest);
    return Array.from(new Uint8Array(await digest.digest), byte => byte.toString(16).padStart(2, '0')).join('');
}

/** PUT /v1/external-media: stores the body once and returns what a plan binds. */
export async function uploadMedia(env: Env, caller: ActionCaller, request: Request): Promise<MediaView> {
    if (writesRefused(env)) fail(503, 'maintenance', 30);
    const bucket = env.EXTERNAL_MEDIA;
    if (!bucket) fail(404, 'unavailable');
    const contentType = (request.headers.get('Content-Type') ?? '').split(';')[0].trim().toLowerCase();
    if (!contentTypes.has(contentType)) invalidField('Content-Type', 'must be video/mp4, video/quicktime or video/webm');
    const declared = Number(request.headers.get('Content-Length'));
    if (!Number.isSafeInteger(declared) || declared < 1) invalidField('Content-Length', 'must give the exact body size');
    if (declared > maxMediaBytes) fail(413, 'tooLarge');
    if (!request.body) invalidField('body', 'is required');
    // The caller's authority is checked before any byte is stored.
    await atomic(env.db, tx => caller.check(tx));
    const mediaId = crypto.randomUUID(), key = objectKey(caller.accountId, mediaId);
    // A fresh random key with onlyIf never replaces an existing object.
    const stored = await bucket.put(key, request.body, {onlyIf: {etagDoesNotMatch: '*'}, httpMetadata: {contentType}});
    if (!stored || stored.size !== declared) { await bucket.delete(key); invalidField('body', 'must be exactly Content-Length bytes'); }
    // Hash what R2 holds, not what was claimed, so the plan binds the bytes YouTube will receive.
    const object = await bucket.get(key);
    if (!object) fail(503, 'storage');
    const sha256 = await sha256Of(object.body);
    return atomic(env.db, async tx => {
        await caller.check(tx);
        const at = tx.store.now;
        tx.store.stage(tx.store.orm.insert(schema.externalMedia).values({media_id: mediaId, account_id: caller.accountId,
            content_type: contentType, bytes: stored.size, sha256, created_at: at}));
        return {mediaId, contentType, bytes: stored.size, sha256, createdAt: new Date(Number(at / 10000n)).toISOString()};
    });
}

/** The agent's own media record, or null; another agent's media is indistinguishable from a missing one. */
export async function readMediaRecord(tx: Transaction, accountId: string, mediaId: string): Promise<StoredMedia | null> {
    if (!mediaIdPattern.test(mediaId)) return null;
    const row = await tx.db.prepare('SELECT content_type,bytes,sha256 FROM external_media WHERE media_id=? AND account_id=?')
        .bind(mediaId, accountId).first<{content_type: string; bytes: number; sha256: string}>();
    return row ? {contentType: row.content_type, bytes: row.bytes, sha256: row.sha256} : null;
}

/** The stored bytes for dispatch; null when the object is gone. */
export async function openMedia(env: Env, accountId: string, mediaId: string): Promise<{body: ReadableStream; size: number} | null> {
    return env.EXTERNAL_MEDIA ? env.EXTERNAL_MEDIA.get(objectKey(accountId, mediaId)) : null;
}
