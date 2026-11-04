/**
 * YouTube through Google OAuth. Connecting exchanges an authorization code for a refresh token and reads the channel
 * that token acts for; that channel is the target account an approval binds. Endpoints are fixed, never configuration.
 * Tokens stay inside these functions: never logged, returned to a caller other than the vault, or put in an error.
 */
import type { Env } from './database.ts';
import type { ExternalProvider } from './external-providers.ts';
import { openMedia, readMediaRecord } from './external-media.ts';

export const youtubeScopes = ['https://www.googleapis.com/auth/youtube.upload', 'https://www.googleapis.com/auth/youtube.readonly'];
const tokenEndpoint = 'https://oauth2.googleapis.com/token';
const channelsEndpoint = 'https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true';
const timeoutMs = 5_000, maxJsonBytes = 64 * 1024;

export type ConnectedAccount = {secret: string; targetAccount: string; targetDisplay: string};
export type ConnectRefusal = 'provider-refused' | 'no-channel' | 'missing-scope';

export const youtubeConfigured = (env: Env) =>
    !!env.GOOGLE_CLIENT_ID && !!env.GOOGLE_CLIENT_SECRET && !!env.EXTERNAL_CREDENTIAL_KEY;

/** A bounded JSON body under one deadline; null for a non-JSON, oversized, stalled or malformed body. */
async function boundedJson(response: Response): Promise<Record<string, unknown> | null> {
    if (!/^application\/json(?:;|$)/i.test(response.headers.get('Content-Type') ?? '')) { void response.body?.cancel().catch(() => {}); return null; }
    const reader = response.body?.getReader();
    if (!reader) return null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const deadline = new Promise<null>(resolve => { timer = setTimeout(() => resolve(null), timeoutMs); });
    const chunks: Uint8Array[] = []; let bytes = 0;
    try {
        while (true) {
            const chunk = await Promise.race([reader.read(), deadline]);
            if (chunk === null) return null;
            if (chunk.done) break;
            bytes += chunk.value.byteLength;
            if (bytes > maxJsonBytes) return null;
            chunks.push(chunk.value);
        }
        const all = new Uint8Array(bytes); let offset = 0;
        for (const chunk of chunks) { all.set(chunk, offset); offset += chunk.byteLength; }
        const value = JSON.parse(new TextDecoder('utf-8', {fatal: true}).decode(all)) as unknown;
        return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null;
    } catch { return null; }
    finally {
        if (timer !== undefined) clearTimeout(timer);
        void reader.cancel().catch(() => {});
    }
}

const send = (url: string, init: RequestInit) => fetch(url, {...init, redirect: 'manual', signal: AbortSignal.timeout(timeoutMs)});

/** Exchanges the code (PKCE) and reads the token's channel; a refusal names why, never what Google returned. */
export async function connectYoutube(env: Env, input: {code: string; verifier: string; redirectUri: string}): Promise<ConnectedAccount | ConnectRefusal> {
    // Never follow a redirect with the client secret, code or verifier.
    const exchange = await send(tokenEndpoint, {method: 'POST', headers: {'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json'},
        body: new URLSearchParams({client_id: env.GOOGLE_CLIENT_ID!, client_secret: env.GOOGLE_CLIENT_SECRET!, code: input.code,
            code_verifier: input.verifier, grant_type: 'authorization_code', redirect_uri: input.redirectUri})});
    const grant = exchange.ok ? await boundedJson(exchange) : (void exchange.body?.cancel().catch(() => {}), null);
    const accessToken = grant?.access_token, refreshToken = grant?.refresh_token, scope = grant?.scope;
    if (typeof accessToken !== 'string' || !accessToken || typeof refreshToken !== 'string' || !refreshToken) return 'provider-refused';
    // The owner may untick a scope on Google's consent screen; anything short of both is not a usable credential.
    const granted = new Set(typeof scope === 'string' ? scope.split(' ') : []);
    if (!youtubeScopes.every(required => granted.has(required))) return 'missing-scope';
    const response = await send(channelsEndpoint, {headers: {Authorization: 'Bearer ' + accessToken, Accept: 'application/json'}});
    const channels = response.ok ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null);
    if (!channels) return 'provider-refused';
    const items = Array.isArray(channels.items) ? channels.items as unknown[] : [];
    // One channel per token: a brand account picked on the consent screen is its own channel.
    if (items.length !== 1) return 'no-channel';
    const channel = items[0] as {id?: unknown; snippet?: {title?: unknown}};
    const id = channel.id, title = channel.snippet?.title;
    if (typeof id !== 'string' || !/^UC[A-Za-z0-9_-]{22}$/.test(id) || typeof title !== 'string') return 'provider-refused';
    return {secret: refreshToken, targetAccount: id, targetDisplay: title.slice(0, 200)};
}

// ---- upload_video ---------------------------------------------------------------------------------------------------
const uploadInit = 'https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status';
const uploadSessionPrefix = 'https://www.googleapis.com/upload/youtube/v3/videos?';
const uploadTimeoutMs = 15 * 60_000;
const privacyStatuses = new Set(['private', 'unlisted', 'public']);
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/, sha = /^[0-9a-f]{64}$/;

/** What an owner approves for one upload. The media is named by ID and pinned by its size and SHA-256. */
export type VideoPlan = {title: string; description: string; privacyStatus: 'private' | 'unlisted' | 'public'; mediaId: string; bytes: number; sha256: string};

/** The content of an upload_video action, or null when it is not exactly this shape. */
export function videoPlan(content: string): VideoPlan | null {
    let value: unknown;
    try { value = JSON.parse(content); } catch { return null; }
    const plan = value as Record<string, unknown>;
    if (!plan || typeof plan !== 'object' || Array.isArray(plan) || Object.keys(plan).sort().join() !== 'bytes,description,mediaId,privacyStatus,sha256,title') return null;
    const {title, description, privacyStatus, mediaId, bytes, sha256} = plan;
    // YouTube's own limits: a 1-100 character title without angle brackets, a description of at most 5000 bytes.
    if (typeof title !== 'string' || title.trim() === '' || title.length > 100 || /[<>]/.test(title)) return null;
    if (typeof description !== 'string' || new TextEncoder().encode(description).byteLength > 5000 || /[<>]/.test(description)) return null;
    if (typeof privacyStatus !== 'string' || !privacyStatuses.has(privacyStatus)) return null;
    if (typeof mediaId !== 'string' || !uuid.test(mediaId) || typeof sha256 !== 'string' || !sha.test(sha256)) return null;
    if (typeof bytes !== 'number' || !Number.isSafeInteger(bytes) || bytes < 1) return null;
    return {title, description, privacyStatus: privacyStatus as VideoPlan['privacyStatus'], mediaId, bytes, sha256};
}

/** A fresh access token from the sealed refresh token; null when Google refuses it (nothing has been uploaded). */
async function accessToken(env: Env, refreshToken: string): Promise<string | null> {
    try {
        const response = await send(tokenEndpoint, {method: 'POST', headers: {'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json'},
            body: new URLSearchParams({client_id: env.GOOGLE_CLIENT_ID!, client_secret: env.GOOGLE_CLIENT_SECRET!, refresh_token: refreshToken, grant_type: 'refresh_token'})});
        const grant = response.ok ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null);
        return typeof grant?.access_token === 'string' && grant.access_token ? grant.access_token : null;
    } catch { return null; }
}

export const youtubeUpload: ExternalProvider & {available(env: Env): boolean} = {
    name: 'youtube',
    contractRevision: 'youtube-upload-v1',
    operations: ['upload_video'],
    available: env => youtubeConfigured(env) && !!env.EXTERNAL_MEDIA,
    async checkContent(tx, {accountId, content}) {
        const plan = videoPlan(content);
        if (!plan) return 'must be JSON {title, description, privacyStatus, mediaId, bytes, sha256} within YouTube limits';
        const media = await readMediaRecord(tx, accountId, plan.mediaId);
        // The plan must name the exact uploaded bytes, so the owner approves this file and no other.
        if (!media || media.bytes !== plan.bytes || media.sha256 !== plan.sha256 || !media.contentType.startsWith('video/'))
            return 'must name your uploaded video with its exact bytes and sha256';
        return null;
    },
    async execute(env, call) {
        const plan = videoPlan(call.content);
        if (!plan || call.secret === null) return {status: 'rejected', reason: 'plan-unreadable'};
        const token = await accessToken(env, call.secret);
        if (!token) return {status: 'rejected', reason: 'credential-refused'};
        const media = await openMedia(env, call.accountId, plan.mediaId);
        if (!media || media.size !== plan.bytes) { void media?.body.cancel().catch(() => {}); return {status: 'rejected', reason: 'media-unavailable'}; }
        // Starting a resumable session creates no video; a refused or failed start sent nothing.
        let session: string | null = null;
        try {
            const start = await send(uploadInit, {method: 'POST', headers: {Authorization: 'Bearer ' + token, 'Content-Type': 'application/json; charset=UTF-8',
                'X-Upload-Content-Length': String(plan.bytes), 'X-Upload-Content-Type': 'video/*'},
                body: JSON.stringify({snippet: {title: plan.title, description: plan.description}, status: {privacyStatus: plan.privacyStatus, selfDeclaredMadeForKids: false}})});
            void start.body?.cancel().catch(() => {});
            const location = start.headers.get('Location');
            if (start.status === 200 && location?.startsWith(uploadSessionPrefix)) session = location;
        } catch { /* reported as a refusal below */ }
        if (session === null) { void media.body.cancel().catch(() => {}); return {status: 'rejected', reason: 'upload-refused'}; }
        // From here the video may exist: any failure propagates and is recorded as unknown, never retried.
        const upload = await fetch(session, {method: 'PUT', redirect: 'manual', signal: AbortSignal.timeout(uploadTimeoutMs),
            headers: {Authorization: 'Bearer ' + token, 'Content-Length': String(plan.bytes), 'Content-Type': 'video/*'}, body: media.body});
        const video = upload.status === 200 || upload.status === 201 ? await boundedJson(upload) : null;
        if (typeof video?.id !== 'string' || !/^[A-Za-z0-9_-]{11}$/.test(video.id)) throw Error('Unconfirmed upload response.');
        return {status: 'completed', receipt: video.id};
    }
};
