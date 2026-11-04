/**
 * Media an owned agent uploads for an external action (for example a video). Bytes live in R2 under a random ID that is
 * written once; D1 records the owner, size, type and SHA-256, which an action's plan binds. The route never overwrites.
 */
import * as schema from './schema.ts';
import { atomic, type Env, type Transaction } from './database.ts';
import { writesRefused } from './humans.ts';
import type { ActionCaller } from './external-actions.ts';
import { Problem, fail, invalidField, object } from './wire.ts';
import { base64url, fromBase64url } from './jose.ts';
import { grantCaller } from './client-grants.ts';
import { apiTime } from './time.ts';

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
const contentTypes = new Set(['video/mp4', 'video/quicktime', 'video/webm', 'image/jpeg', 'image/png', 'image/gif', 'image/webp']);
const contentTypeList = 'video/mp4, video/quicktime, video/webm, image/jpeg, image/png, image/gif or image/webp';
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
    const contentType = (request.headers.get('Content-Type') ?? '').split(';')[0].trim().toLowerCase();
    if (!contentTypes.has(contentType)) invalidField('Content-Type', 'must be ' + contentTypeList);
    return storeMedia(env, caller, crypto.randomUUID(), contentType, declaredLength(request), request.body);
}

function declaredLength(request: Request): number {
    const declared = Number(request.headers.get('Content-Length'));
    if (!Number.isSafeInteger(declared) || declared < 1) invalidField('Content-Length', 'must give the exact body size');
    if (declared > maxMediaBytes) fail(413, 'tooLarge');
    return declared;
}

/** Stores `body` once under `mediaId` and records it. An existing object under that ID is never replaced. */
async function storeMedia(env: Env, caller: ActionCaller, mediaId: string, contentType: string, declared: number, body: ReadableStream | null): Promise<MediaView> {
    const bucket = env.EXTERNAL_MEDIA;
    if (!bucket) fail(404, 'unavailable');
    if (!body) invalidField('body', 'is required');
    // The caller's authority is checked before any byte is stored.
    await atomic(env.db, tx => caller.check(tx));
    const key = objectKey(caller.accountId, mediaId);
    // onlyIf refuses when the key already holds an object; that object is someone's upload and is left untouched.
    const stored = await bucket.put(key, body, {onlyIf: {etagDoesNotMatch: '*'}, httpMetadata: {contentType}});
    if (!stored) fail(409, 'conflict');
    if (stored.size !== declared) { await bucket.delete(key); invalidField('body', 'must be exactly Content-Length bytes'); }
    // Hash what R2 holds, not what was claimed, so the plan binds the bytes the provider will receive.
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

/** GET /v1/external-media/{mediaId}: the agent's own media record; another agent's media reads as missing. */
export async function getMedia(env: Env, caller: ActionCaller, mediaId: string): Promise<MediaView> {
    return atomic(env.db, async tx => {
        await caller.check(tx);
        const row = mediaIdPattern.test(mediaId) ? await tx.db.prepare('SELECT content_type,bytes,sha256,CAST(created_at AS TEXT) created_at FROM external_media WHERE media_id=? AND account_id=?')
            .bind(mediaId, caller.accountId).first<{content_type: string; bytes: number; sha256: string; created_at: string}>() : null;
        if (!row) fail(404, 'unavailable');
        return {mediaId, contentType: row.content_type, bytes: row.bytes, sha256: row.sha256, createdAt: apiTime(row.created_at)!};
    });
}

// ---- import from a public URL -----------------------------------------------------------------------------------------
const importTimeoutMs = 5 * 60_000, maxImportUrl = 2048;
const blockedSuffixes = ['.localhost', '.local', '.internal', '.home.arpa'];

/** An https URL on the default port naming a public DNS host: no IP literal, single-label or local name, no credentials. */
function importUrl(env: Env, raw: unknown): URL {
    const reason = 'must be a public https URL with a host name, no credentials and the default port';
    if (typeof raw !== 'string' || raw.length > maxImportUrl || !URL.canParse(raw)) invalidField('url', reason);
    // One trailing dot names the same DNS host ("localhost." is localhost), so it is removed before every check.
    const url = new URL(raw), host = url.hostname.toLowerCase().replace(/\.$/, '');
    // WHATWG parsing already turns every IPv4 spelling into dotted decimal and IPv6 into brackets.
    if (url.protocol !== 'https:' || url.port !== '' || url.username !== '' || url.password !== '' || host.startsWith('[') || /^[0-9.]+$/.test(host)
        || !host.includes('.') || blockedSuffixes.some(suffix => host.endsWith(suffix))) invalidField('url', reason);
    // Never this Worker itself: its signed media URLs are for providers, not for copying between agents.
    if (env.EXTERNAL_MEDIA_ORIGIN && host === new URL(env.EXTERNAL_MEDIA_ORIGIN).hostname.toLowerCase().replace(/\.$/, '')) invalidField('url', reason);
    return url;
}

/**
 * POST /v1/external-media/imports {url}: Weave fetches one public file, without following redirects, and stores it as
 * the caller's media exactly as an upload would. For agents that hold a link (for example to a generated video) but
 * cannot send the file themselves. The response must be 200 with a supported Content-Type and a Content-Length.
 */
export async function importMedia(env: Env, caller: ActionCaller, input: unknown): Promise<MediaView> {
    if (writesRefused(env)) fail(503, 'maintenance', 30);
    object(input, ['url']);
    const url = importUrl(env, (input as {url?: unknown}).url);
    if (!env.EXTERNAL_MEDIA) fail(404, 'unavailable');
    // No outbound request is made for a caller without authority.
    await atomic(env.db, tx => caller.check(tx));
    let response: Response;
    try {
        // identity encoding keeps Content-Length the size of the stored file.
        response = await fetch(url.href, {redirect: 'manual', signal: AbortSignal.timeout(importTimeoutMs),
            headers: {Accept: [...contentTypes].join(', '), 'Accept-Encoding': 'identity'}});
    } catch { invalidField('url', 'could not be fetched'); }
    const refuse: (why: string) => never = why => { void response.body?.cancel().catch(() => {}); invalidField('url', why); };
    if (response.status !== 200) refuse(response.status >= 300 && response.status < 400 ? 'must not redirect' : `answered ${response.status}, not 200`);
    const contentType = (response.headers.get('Content-Type') ?? '').split(';')[0].trim().toLowerCase();
    if (!contentTypes.has(contentType)) refuse('must serve ' + contentTypeList);
    const declared = Number(response.headers.get('Content-Length') ?? NaN);
    if (!Number.isSafeInteger(declared) || declared < 1 || declared > maxMediaBytes) refuse(`must state a Content-Length of at most ${maxMediaBytes}`);
    const sourceBody = response.body;
    if (!sourceBody) refuse('must have a body');
    // Only a failure of the source itself is blamed on the URL; storage and database errors keep their own handling.
    let sourceFailed = false, delivered = 0;
    const reader = sourceBody.getReader();
    const source = new ReadableStream<Uint8Array>({
        async pull(controller) {
            try {
                const chunk = await reader.read();
                if (!chunk.done) delivered += chunk.value.byteLength;
                if (delivered > declared || (chunk.done && delivered !== declared)) throw Error('Source body is not its stated Content-Length.');
                if (chunk.done) controller.close(); else controller.enqueue(chunk.value);
            } catch (error) { sourceFailed = true; controller.error(error); void reader.cancel().catch(() => {}); }
        },
        cancel: reason => reader.cancel(reason)
    });
    // R2 needs a stream of known length, which FixedLengthStream gives in the Workers runtime.
    const Fixed = (globalThis as {FixedLengthStream?: new (length: number) => TransformStream<Uint8Array, Uint8Array>}).FixedLengthStream;
    try {
        return await storeMedia(env, caller, crypto.randomUUID(), contentType, declared, Fixed ? source.pipeThrough(new Fixed(declared)) : source);
    } catch (error) {
        if (sourceFailed && !(error instanceof Problem)) invalidField('url', 'could not be fetched completely');
        throw error;
    }
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

// ---- signed fetch URLs ------------------------------------------------------------------------------------------------
/** How long a provider may take to start fetching media for one dispatch. */
export const mediaUrlLifetimeMs = 15 * 60_000;
const signatureLabel = 'weave-media-url-v1';

async function urlKey(env: Env): Promise<CryptoKey> {
    const raw = env.EXTERNAL_CREDENTIAL_KEY === undefined ? null : fromBase64url(env.EXTERNAL_CREDENTIAL_KEY);
    if (!raw || raw.byteLength !== 32) throw Error('EXTERNAL_CREDENTIAL_KEY must be 32 bytes of base64url.');
    // Domain-separated from sealing: an HMAC over a labelled message, never the AES key use.
    return crypto.subtle.importKey('raw', raw, {name: 'HMAC', hash: 'SHA-256'}, false, ['sign', 'verify']);
}
const signedText = (accountId: string, mediaId: string, expiresAt: number) => [signatureLabel, accountId, mediaId, String(expiresAt)].join('\n');

/**
 * A short-lived public URL for one stored object, for a provider that fetches media itself. Minted only while dispatching
 * an approved action; the signature binds the agent, the media and the expiry.
 */
export async function signedMediaUrl(env: Env, accountId: string, mediaId: string, now: number): Promise<string> {
    if (!env.EXTERNAL_MEDIA_ORIGIN) throw Error('EXTERNAL_MEDIA_ORIGIN is not configured.');
    const expiresAt = now + mediaUrlLifetimeMs;
    const signature = base64url(new Uint8Array(await crypto.subtle.sign('HMAC', await urlKey(env), new TextEncoder().encode(signedText(accountId, mediaId, expiresAt)))));
    const url = new URL(`/v1/external-media/${mediaId}/content`, env.EXTERNAL_MEDIA_ORIGIN);
    url.searchParams.set('account', accountId); url.searchParams.set('expires', String(expiresAt)); url.searchParams.set('signature', signature);
    return url.href;
}

/** GET /v1/external-media/{mediaId}/content with a valid, unexpired signature; anything else is 404. */
export async function serveSignedMedia(env: Env, mediaId: string, url: URL, now: number): Promise<Response> {
    const accountId = url.searchParams.get('account'), expires = Number(url.searchParams.get('expires')), signature = url.searchParams.get('signature');
    const bytes = signature === null ? null : fromBase64url(signature);
    if (!accountId || !mediaIdPattern.test(mediaId) || !Number.isSafeInteger(expires) || expires <= now || !bytes || [...url.searchParams.keys()].length !== 3) fail(404, 'unavailable');
    if (!await crypto.subtle.verify('HMAC', await urlKey(env), bytes, new TextEncoder().encode(signedText(accountId, mediaId, expires)))) fail(404, 'unavailable');
    const record = await atomic(env.db, tx => readMediaRecord(tx, accountId, mediaId));
    const object = record && await openMedia(env, accountId, mediaId);
    if (!record || !object) fail(404, 'unavailable');
    return new Response(object.body, {headers: {'Content-Type': record.contentType, 'Content-Length': String(object.size), 'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff', 'Content-Disposition': 'attachment'}});
}

// ---- signed upload URLs ---------------------------------------------------------------------------------------------
/** How long a minted upload URL may start a PUT; the body may take longer to arrive. */
export const uploadUrlLifetimeMs = 15 * 60_000;
const uploadLabel = 'weave-media-upload-v1';
export type MediaUpload = {mediaId: string; uploadUrl: string; contentType: string; bytes: number; expiresAt: string};
const uploadText = (accountId: string, grantId: string, mediaId: string, contentType: string, bytes: number, expiresAt: number) =>
    [uploadLabel, accountId, grantId, mediaId, contentType, String(bytes), String(expiresAt)].join('\n');
const grantPattern = /^[A-Za-z0-9._:-]{1,128}$/;

/**
 * A short-lived URL that accepts one PUT of exactly `bytes` of `contentType` for an agent acting through an OAuth consent
 * grant, which has no request-signing key. The signature binds the agent, the grant, the media ID, type, size and expiry;
 * the PUT re-checks the grant, and the media ID can be written only once.
 */
export async function createMediaUpload(env: Env, accountId: string, grantId: string, contentType: unknown, bytes: unknown, now: number): Promise<MediaUpload> {
    if (writesRefused(env)) fail(503, 'maintenance', 30);
    if (typeof contentType !== 'string' || !contentTypes.has(contentType)) invalidField('contentType', 'must be ' + contentTypeList);
    if (typeof bytes !== 'number' || !Number.isSafeInteger(bytes) || bytes < 1 || bytes > maxMediaBytes) invalidField('bytes', `must be the exact file size, 1 to ${maxMediaBytes}`);
    if (!env.EXTERNAL_MEDIA || !env.EXTERNAL_MEDIA_ORIGIN || !env.EXTERNAL_CREDENTIAL_KEY || !grantPattern.test(grantId)) fail(404, 'unavailable');
    await atomic(env.db, tx => grantCaller(accountId, grantId).check(tx));
    const mediaId = crypto.randomUUID(), expiresAt = now + uploadUrlLifetimeMs;
    const signature = base64url(new Uint8Array(await crypto.subtle.sign('HMAC', await urlKey(env),
        new TextEncoder().encode(uploadText(accountId, grantId, mediaId, contentType, bytes, expiresAt)))));
    const url = new URL(`/v1/external-media/${mediaId}/content`, env.EXTERNAL_MEDIA_ORIGIN);
    for (const [name, field] of [['account', accountId], ['grant', grantId], ['type', contentType], ['bytes', String(bytes)], ['expires', String(expiresAt)], ['signature', signature]])
        url.searchParams.set(name, field);
    return {mediaId, uploadUrl: url.href, contentType, bytes, expiresAt: new Date(expiresAt).toISOString()};
}

/** PUT /v1/external-media/{mediaId}/content with a valid upload signature; an invalid or expired signature is 404. */
export async function receiveSignedUpload(env: Env, mediaId: string, url: URL, request: Request, now: number): Promise<MediaView> {
    if (writesRefused(env)) fail(503, 'maintenance', 30);
    const param = (name: string) => url.searchParams.get(name);
    const accountId = param('account'), grantId = param('grant'), contentType = param('type'), bytes = Number(param('bytes')), expires = Number(param('expires'));
    const signature = param('signature') === null ? null : fromBase64url(param('signature')!);
    if (!accountId || !mediaIdPattern.test(accountId) || !grantId || !grantPattern.test(grantId) || !contentType || !contentTypes.has(contentType)
        || !Number.isSafeInteger(bytes) || bytes < 1 || !Number.isSafeInteger(expires) || expires <= now || !signature
        || !mediaIdPattern.test(mediaId) || [...url.searchParams.keys()].length !== 6) fail(404, 'unavailable');
    if (!await crypto.subtle.verify('HMAC', await urlKey(env), signature, new TextEncoder().encode(uploadText(accountId, grantId, mediaId, contentType, bytes, expires))))
        fail(404, 'unavailable');
    // The request must carry exactly what was signed.
    if ((request.headers.get('Content-Type') ?? '').split(';')[0].trim().toLowerCase() !== contentType) invalidField('Content-Type', 'must be ' + contentType);
    if (declaredLength(request) !== bytes) invalidField('Content-Length', 'must be ' + bytes);
    return storeMedia(env, grantCaller(accountId, grantId), mediaId, contentType, bytes, request.body);
}
