/**
 * Bluesky as a native publishing adapter, through the AT Protocol XRPC API of the bsky.social entryway. The owner
 * connects with an app password (never the account password); each dispatch opens a short session with it. Endpoints are
 * fixed, never configuration. The app password and session tokens stay in these functions and the vault.
 */
import { openMedia } from './external-media.ts';
import type { PublishingAdapter } from './publishing.ts';

const entryway = 'https://bsky.social/xrpc/';
const timeoutMs = 15_000, maxJsonBytes = 64 * 1024;
/** Bluesky's limits: 300 graphemes and 3000 bytes of text, four images of at most 1,000,000 bytes each. */
const maxImageBytes = 1_000_000;
/** App passwords look like xxxx-xxxx-xxxx-xxxx; requiring the shape keeps an account password out of the vault. */
export const appPasswordPattern = /^[a-z0-9]{4}(?:-[a-z0-9]{4}){3}$/;
const handlePattern = /^(?=.{3,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/;
const didPattern = /^did:(?:plc:[a-z2-7]{24}|web:[a-z0-9.-]{3,40})$/;
const recordKey = /^(?!\.{1,2}$)[A-Za-z0-9._:~-]+$/;

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
async function verify(handle: string, appPassword: string) {
    const identifier = handle.trim().toLowerCase().replace(/^@/, '');
    if (!handlePattern.test(identifier) || !appPasswordPattern.test(appPassword)) return null;
    const session = await createSession(identifier, appPassword);
    return session && [{ref: session.did, targetAccount: session.did, targetDisplay: '@' + session.handle}];
}

const closers: Record<string, string> = {')': '(', ']': '[', '}': '{'};
/** A link without the sentence punctuation after it. A closing bracket stays when the link opened it, as in Function_(mathematics). */
function trimLink(raw: string): string {
    let url = raw;
    while (url.length > 0) {
        const last = url[url.length - 1];
        const opener = closers[last];
        if (opener !== undefined && url.split(opener).length > url.split(last).length - 1) break;
        if (opener === undefined && !".,;:!?'\"".includes(last)) break;
        url = url.slice(0, -1);
    }
    return url;
}

type Facet = {index: {byteStart: number; byteEnd: number}; features: Record<string, string>[]};
/** Clickable links and hashtags: Bluesky renders plain text unless byte ranges mark them. Mentions are left as text. */
export function facets(text: string): Facet[] {
    const encoder = new TextEncoder(), result: Facet[] = [];
    const byteIndex = (index: number) => encoder.encode(text.slice(0, index)).byteLength;
    for (const match of text.matchAll(/https?:\/\/[^\s<>"]+/g)) {
        const url = trimLink(match[0]);
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

export const bluesky: PublishingAdapter = {
    name: 'bluesky',
    contractRevision: 'bluesky-post-v1',
    rules: {
        text: {max: 300, unit: 'graphemes', maxBytes: 3000},
        media: {max: 4, types: {'image/jpeg': maxImageBytes, 'image/png': maxImageBytes, 'image/webp': maxImageBytes}},
        schedule: null,
        note: 'Links and #tags become clickable.'
    },
    connector: {
        label: 'Bluesky', replaces: false,
        fields: [{name: 'handle', label: 'Bluesky handle', secret: false, placeholder: 'name.bsky.social'},
            {name: 'appPassword', label: 'App password', secret: true, pattern: '[a-z0-9]{4}(-[a-z0-9]{4}){3}'}],
        help: 'Create an app password in Bluesky under Settings, Privacy and security, App passwords. Never enter your account password.',
        verify: async ({handle, appPassword}) => typeof handle === 'string' && typeof appPassword === 'string' ? verify(handle, appPassword) : null
    },
    async publish({env, accountId, targetAccount, secret, post}) {
        // Every step before createRecord publishes nothing, so each failure there is a clean refusal.
        const session = await createSession(targetAccount, secret);
        if (!session || session.did !== targetAccount) return {status: 'rejected', reason: 'credential-refused'};
        const auth = {Authorization: 'Bearer ' + session.accessJwt};
        const images: {alt: string; image: unknown}[] = [];
        for (const file of post.media) {
            const record = await openMedia(env, accountId, file.mediaId);
            if (!record || record.size !== file.bytes) { void record?.body.cancel().catch(() => {}); return {status: 'rejected', reason: 'media-unavailable'}; }
            try {
                const response = await xrpc('com.atproto.repo.uploadBlob', {method: 'POST', body: record.body, timeout: 60_000,
                    headers: {...auth, 'Content-Type': file.contentType, 'Content-Length': String(file.bytes)}});
                const uploaded = response.ok ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null);
                if (!uploaded?.blob || typeof uploaded.blob !== 'object') return {status: 'rejected', reason: 'media-upload-refused'};
                images.push({alt: file.alt ?? '', image: uploaded.blob});
            } catch { return {status: 'rejected', reason: 'media-upload-refused'}; }
        }
        const record: Record<string, unknown> = {$type: 'app.bsky.feed.post', text: post.text, createdAt: new Date().toISOString()};
        const links = facets(post.text);
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
        const prefix = 'at://' + session.did + '/app.bsky.feed.post/';
        // The receipt must be one post's AT-URI with an atproto record key, so apps can link it.
        if (typeof uri !== 'string' || uri.length > 256 || !uri.startsWith(prefix) || !recordKey.test(uri.slice(prefix.length))) throw Error('Unconfirmed Bluesky response.');
        return {status: 'completed', receipt: uri};
    }
};
