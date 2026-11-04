/**
 * Bluesky as a native publishing provider, through the AT Protocol XRPC API of the bsky.social entryway. The owner
 * connects with an app password (never the account password); each dispatch opens a short session with it. Endpoints are
 * fixed, never configuration. The app password and session tokens stay in these functions and the vault.
 */
import { atomic } from './database.ts';
import type { ExternalProvider } from './external-providers.ts';
import { openMedia, readMediaRecord } from './external-media.ts';

const entryway = 'https://bsky.social/xrpc/';
const timeoutMs = 15_000, maxJsonBytes = 64 * 1024;
/** Bluesky's limits: 300 graphemes of text, four images of at most 1,000,000 bytes each. */
const maxGraphemes = 300, maxTextBytes = 3000, maxImages = 4, maxImageBytes = 1_000_000, maxAlt = 2000;
const imageTypes = new Set(['image/jpeg', 'image/png', 'image/webp']);
/** App passwords look like xxxx-xxxx-xxxx-xxxx; requiring the shape keeps an account password out of the vault. */
export const appPasswordPattern = /^[a-z0-9]{4}(?:-[a-z0-9]{4}){3}$/;
const handlePattern = /^(?=.{3,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/;
const didPattern = /^did:(?:plc:[a-z2-7]{24}|web:[a-z0-9.-]{3,40})$/;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/, sha = /^[0-9a-f]{64}$/;

export type BlueskyAccount = {did: string; handle: string};
export type BlueskyPlan = {text: string; media: {mediaId: string; bytes: number; sha256: string; alt: string}[]};

async function boundedJson(response: Response): Promise<Record<string, unknown> | null> {
    const reader = response.body?.getReader();
    if (!reader) return null;
    const chunks: Uint8Array[] = []; let bytes = 0;
    try {
        while (true) {
            const chunk = await reader.read();
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
    finally { void reader.cancel().catch(() => {}); }
}
const xrpc = (method: string, init: RequestInit & {timeout?: number} = {}) =>
    fetch(entryway + method, {...init, redirect: 'manual', signal: AbortSignal.timeout(init.timeout ?? timeoutMs)});

/** A session for `identifier` (handle or DID); null when Bluesky refuses it. Opening a session posts nothing. */
async function createSession(identifier: string, appPassword: string): Promise<{did: string; handle: string; accessJwt: string} | null> {
    try {
        const response = await xrpc('com.atproto.server.createSession', {method: 'POST', headers: {'Content-Type': 'application/json', Accept: 'application/json'},
            body: JSON.stringify({identifier, password: appPassword})});
        const session = response.ok ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null);
        const {did, handle, accessJwt, active} = session ?? {};
        if (typeof did !== 'string' || !didPattern.test(did) || typeof handle !== 'string' || !handlePattern.test(handle)
            || typeof accessJwt !== 'string' || !accessJwt || active === false) return null;
        return {did, handle, accessJwt};
    } catch { return null; }
}

/** Connect: verifies the app password and reads the account it acts as; null when Bluesky refuses or the input is not an app password. */
export async function connectBluesky(handle: string, appPassword: string): Promise<BlueskyAccount | null> {
    const identifier = handle.trim().toLowerCase().replace(/^@/, '');
    if (!handlePattern.test(identifier) || !appPasswordPattern.test(appPassword)) return null;
    const session = await createSession(identifier, appPassword);
    return session && {did: session.did, handle: session.handle};
}

const graphemes = (text: string) => [...new Intl.Segmenter(undefined, {granularity: 'grapheme'}).segment(text)].length;

/** The content of a publish_post action, or null when it is not exactly this shape within Bluesky's limits. */
export function blueskyPlan(content: string): BlueskyPlan | null {
    let value: unknown;
    try { value = JSON.parse(content); } catch { return null; }
    const plan = value as Record<string, unknown>;
    if (!plan || typeof plan !== 'object' || Array.isArray(plan) || Object.keys(plan).sort().join() !== 'media,text') return null;
    const {text, media} = plan;
    if (typeof text !== 'string' || graphemes(text) > maxGraphemes || new TextEncoder().encode(text).byteLength > maxTextBytes) return null;
    if (!Array.isArray(media) || media.length > maxImages || (text.trim() === '' && media.length === 0)) return null;
    const files: BlueskyPlan['media'] = [];
    for (const file of media as Record<string, unknown>[]) {
        if (!file || typeof file !== 'object' || Object.keys(file).sort().join() !== 'alt,bytes,mediaId,sha256') return null;
        const {mediaId, bytes, sha256, alt} = file;
        if (typeof mediaId !== 'string' || !uuid.test(mediaId) || typeof sha256 !== 'string' || !sha.test(sha256)
            || typeof bytes !== 'number' || !Number.isSafeInteger(bytes) || bytes < 1 || bytes > maxImageBytes
            || typeof alt !== 'string' || alt.length > maxAlt) return null;
        files.push({mediaId, bytes, sha256, alt});
    }
    return {text, media: files};
}

type Facet = {index: {byteStart: number; byteEnd: number}; features: Record<string, string>[]};
/** Clickable links and hashtags: Bluesky renders plain text unless byte ranges mark them. Mentions are left as text. */
export function facets(text: string): Facet[] {
    const encoder = new TextEncoder(), result: Facet[] = [];
    const byteIndex = (index: number) => encoder.encode(text.slice(0, index)).byteLength;
    for (const match of text.matchAll(/https?:\/\/[^\s<>"]+/g)) {
        const url = match[0].replace(/[.,;:!?)\]}'"]+$/, '');
        if (!URL.canParse(url)) continue;
        const start = match.index!;
        result.push({index: {byteStart: byteIndex(start), byteEnd: byteIndex(start + url.length)}, features: [{$type: 'app.bsky.richtext.facet#link', uri: url}]});
    }
    for (const match of text.matchAll(/(^|\s)#([^\s#.,;:!?)\]}'"]*[^\s#.,;:!?)\]}'"\d][^\s#.,;:!?)\]}'"]*)/gu)) {
        const tag = match[2];
        if (tag.length > 64) continue;
        const start = match.index! + match[1].length;
        result.push({index: {byteStart: byteIndex(start), byteEnd: byteIndex(start + 1 + tag.length)}, features: [{$type: 'app.bsky.richtext.facet#tag', tag}]});
    }
    return result;
}

export const blueskyPublish: ExternalProvider = {
    name: 'bluesky',
    contractRevision: 'bluesky-post-v1',
    operations: ['publish_post'],
    describeContent: () => `JSON {"text": at most ${maxGraphemes} characters, "media": up to ${maxImages} of your uploaded JPEG, PNG or WebP images of at most `
        + `${maxImageBytes} bytes as {"mediaId", "bytes", "sha256", "alt"} where alt describes the image for people who cannot see it}. Text or media is required; links and #tags become clickable.`,
    async checkContent(tx, {accountId, content}) {
        const plan = blueskyPlan(content);
        if (!plan) return `must be JSON {text, media} within Bluesky limits: at most ${maxGraphemes} characters and ${maxImages} images of at most ${maxImageBytes} bytes, each with alt text`;
        for (const file of plan.media) {
            const media = await readMediaRecord(tx, accountId, file.mediaId);
            if (!media || media.bytes !== file.bytes || media.sha256 !== file.sha256) return 'media must name your uploaded files with their exact bytes and sha256';
            if (!imageTypes.has(media.contentType)) return 'media must be JPEG, PNG or WebP images for Bluesky';
        }
        return null;
    },
    async execute(env, call) {
        const plan = blueskyPlan(call.content);
        if (!plan || call.secret === null) return {status: 'rejected', reason: 'plan-unreadable'};
        // Every step before createRecord publishes nothing, so each failure there is a clean refusal.
        const session = await createSession(call.targetAccount, call.secret);
        if (!session || session.did !== call.targetAccount) return {status: 'rejected', reason: 'credential-refused'};
        const auth = {Authorization: 'Bearer ' + session.accessJwt};
        const stored = await atomic(env.db, tx => Promise.all(plan.media.map(file => readMediaRecord(tx, call.accountId, file.mediaId))));
        const images: {alt: string; image: unknown}[] = [];
        for (const [index, file] of plan.media.entries()) {
            const type = stored[index]?.contentType;
            if (!type || !imageTypes.has(type)) return {status: 'rejected', reason: 'media-unavailable'};
            const record = await openMedia(env, call.accountId, file.mediaId);
            if (!record || record.size !== file.bytes) { void record?.body.cancel().catch(() => {}); return {status: 'rejected', reason: 'media-unavailable'}; }
            try {
                const response = await xrpc('com.atproto.repo.uploadBlob', {method: 'POST', body: record.body, timeout: 60_000,
                    headers: {...auth, 'Content-Type': type, 'Content-Length': String(file.bytes)}});
                const uploaded = response.ok ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null);
                if (!uploaded?.blob || typeof uploaded.blob !== 'object') return {status: 'rejected', reason: 'media-upload-refused'};
                images.push({alt: file.alt, image: uploaded.blob});
            } catch { return {status: 'rejected', reason: 'media-upload-refused'}; }
        }
        const record: Record<string, unknown> = {$type: 'app.bsky.feed.post', text: plan.text, createdAt: new Date().toISOString()};
        const links = facets(plan.text);
        if (links.length) record.facets = links;
        if (images.length) record.embed = {$type: 'app.bsky.embed.images', images};
        // From here a post may be created. Only a definite refusal is a rejection; anything else is unknown and never re-sent.
        const response = await xrpc('com.atproto.repo.createRecord', {method: 'POST', headers: {...auth, 'Content-Type': 'application/json', Accept: 'application/json'},
            body: JSON.stringify({repo: session.did, collection: 'app.bsky.feed.post', record})});
        if ([400, 401, 403, 413, 429].includes(response.status)) {
            void response.body?.cancel().catch(() => {});
            return {status: 'rejected', reason: 'bluesky-refused-' + response.status};
        }
        const created = response.ok ? await boundedJson(response) : null;
        const uri = created?.uri;
        if (typeof uri !== 'string' || uri.length > 256 || !uri.startsWith('at://' + session.did + '/app.bsky.feed.post/')) throw Error('Unconfirmed Bluesky response.');
        return {status: 'completed', receipt: uri};
    }
};

