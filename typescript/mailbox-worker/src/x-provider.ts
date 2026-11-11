/**
 * X as a native publishing adapter, through the X API v2 with OAuth 1.0a user context. The owner creates an app in
 * the X developer portal and pastes its API key and secret with the access token and secret it generated for their own
 * account; the four values are sealed together and never logged or returned. Endpoints are fixed, never configuration.
 */
import { openMedia } from './external-media.ts';
import type { PublishingAdapter } from './publishing.ts';

const api = 'https://api.x.com/2/';
const timeoutMs = 15_000, uploadTimeoutMs = 120_000, maxJsonBytes = 64 * 1024;
/** X takes images of at most 5 MB through the simple upload; files are read into memory before anything is sent. */
const maxImageBytes = 5_000_000;
/** X's POST /2/media/metadata takes at most 1000 characters of alt text. */
const maxAltText = 1000;
/** X's limit for a post from an account without Premium, in weighted characters. */
const maxText = 280;
const keyPattern = /^[A-Za-z0-9]{10,64}$/, secretPattern = /^[A-Za-z0-9]{30,80}$/, tokenPattern = /^[0-9]{1,20}-[A-Za-z0-9]{10,64}$/;
const idPattern = /^[0-9]{1,19}$/, usernamePattern = /^[A-Za-z0-9_]{1,15}$/;
type XKeys = {apiKey: string; apiKeySecret: string; accessToken: string; accessTokenSecret: string};

/** The four OAuth 1.0a values, or null when any is missing or not shaped like an X key. */
function xKeys(values: Readonly<Record<string, unknown>>): XKeys | null {
    const {apiKey, apiKeySecret, accessToken, accessTokenSecret} = values;
    if (typeof apiKey !== 'string' || typeof apiKeySecret !== 'string' || typeof accessToken !== 'string' || typeof accessTokenSecret !== 'string') return null;
    const keys = {apiKey: apiKey.trim(), apiKeySecret: apiKeySecret.trim(), accessToken: accessToken.trim(), accessTokenSecret: accessTokenSecret.trim()};
    return keyPattern.test(keys.apiKey) && secretPattern.test(keys.apiKeySecret) && tokenPattern.test(keys.accessToken) && secretPattern.test(keys.accessTokenSecret)
        ? keys : null;
}

/** RFC 3986 percent-encoding, as OAuth 1.0a signs. */
const encode = (value: string) => encodeURIComponent(value).replace(/[!'()*]/g, c => '%' + c.charCodeAt(0).toString(16).toUpperCase());

/**
 * The OAuth 1.0a Authorization header for one request. JSON and multipart bodies are not signed, so only the URL's
 * query and the oauth_ parameters are.
 */
async function oauthHeader(keys: XKeys, method: string, url: URL): Promise<string> {
    const nonce = [...crypto.getRandomValues(new Uint8Array(16))].map(b => b.toString(16).padStart(2, '0')).join('');
    const oauth: Record<string, string> = {oauth_consumer_key: keys.apiKey, oauth_nonce: nonce, oauth_signature_method: 'HMAC-SHA1',
        oauth_timestamp: Math.floor(Date.now() / 1000).toString(), oauth_token: keys.accessToken, oauth_version: '1.0'};
    const pairs = [...url.searchParams, ...Object.entries(oauth)].map(([name, value]) => [encode(name), encode(value)])
        .sort(([a, x], [b, y]) => a < b ? -1 : a > b ? 1 : x < y ? -1 : x > y ? 1 : 0);
    const base = [method.toUpperCase(), encode(url.origin + url.pathname), encode(pairs.map(([name, value]) => name + '=' + value).join('&'))].join('&');
    const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(encode(keys.apiKeySecret) + '&' + encode(keys.accessTokenSecret)),
        {name: 'HMAC', hash: 'SHA-1'}, false, ['sign']);
    const signature = btoa(String.fromCharCode(...new Uint8Array(await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(base)))));
    return 'OAuth ' + Object.entries({...oauth, oauth_signature: signature}).map(([name, value]) => `${encode(name)}="${encode(value)}"`).join(', ');
}

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

/** One signed X API call. Network failures throw; the caller classifies the status. */
async function call(keys: XKeys, method: string, path: string, init: {json?: unknown; form?: FormData; timeout?: number} = {}) {
    const url = new URL(api + path);
    const headers: Record<string, string> = {Authorization: await oauthHeader(keys, method, url)};
    if (init.json !== undefined) headers['Content-Type'] = 'application/json';
    return fetch(url, {method, headers, redirect: 'manual', signal: AbortSignal.timeout(init.timeout ?? timeoutMs),
        ...(init.json !== undefined ? {body: JSON.stringify(init.json)} : init.form ? {body: init.form} : {})});
}

const data = (body: Record<string, unknown> | null) =>
    body?.data && typeof body.data === 'object' && !Array.isArray(body.data) ? body.data as Record<string, unknown> : null;

/** The account the keys act as; null when X refuses them or the values are not X keys. Reading it posts nothing. */
async function verify(values: Readonly<Record<string, string>>) {
    const keys = xKeys(values);
    if (!keys) return null;
    try {
        const response = await call(keys, 'GET', 'users/me');
        const user = data(response.status === 200 ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null));
        if (typeof user?.id !== 'string' || !idPattern.test(user.id) || typeof user.username !== 'string' || !usernamePattern.test(user.username)) return null;
        // The access token names its user before the dash; keys for another account are refused rather than relabelled.
        if (keys.accessToken.split('-')[0] !== user.id) return null;
        return [{ref: user.id, targetAccount: user.id, targetDisplay: '@' + user.username}];
    } catch { return null; }
}

const light = (c: number) => c <= 0x10ff || (c >= 0x2000 && c <= 0x200d) || (c >= 0x2010 && c <= 0x201f) || (c >= 0x2032 && c <= 0x2037);
const segmenter = new Intl.Segmenter(undefined, {granularity: 'grapheme'});
/** Shapes X counts as one emoji: a flag, a keycap, a tag flag, or up to four pictographs joined by ZWJ, each with an optional variation selector and skin tone. */
const pictograph = String.raw`\p{Extended_Pictographic}[\ufe0e\ufe0f]?[\u{1f3fb}-\u{1f3ff}]?`;
const emoji = new RegExp(String.raw`^(?:\p{Regional_Indicator}{2}|[0-9#*]\ufe0f?\u20e3|\u{1f3f4}[\u{e0020}-\u{e007e}]{1,7}\u{e007f}|${pictograph}(?:\u200d${pictograph}){0,3})$`, 'u');
/** Code points count 1 or 2; a grapheme of an emoji's shape counts 2 instead, and nothing else is counted for less than its code points. */
function weigh(part: string): number {
    let total = 0;
    for (const {segment} of segmenter.segment(part)) {
        let own = 0;
        for (const char of segment) own += light(char.codePointAt(0)!) ? 1 : 2;
        total += own > 2 && emoji.test(segment) ? 2 : own;
    }
    return total;
}
/** True for `label.label.tld`, labels of ASCII letters, digits and hyphens, the last of letters only. */
function isDomain(host: string): boolean {
    const labels = host.split('.');
    return labels.length >= 2 && labels.every(label => /^[a-z0-9-]+$/i.test(label)) && /^[a-z]{2,}$/i.test(labels[labels.length - 1]);
}
const opening = '("\'<[{', closing = ')"\'>]}.,!?;:';

/** The weight of one run of ASCII URL characters that holds at most one scheme, at its start: a link counts 23. */
function weighRun(run: string): number {
    let from = 0, to = run.length;
    while (from < to && opening.includes(run[from])) from++;
    while (to > from && closing.includes(run[to - 1])) to--;
    const core = run.slice(from, to), scheme = /^https?:\/\//i.exec(core);
    const host = core.slice(scheme ? scheme[0].length : 0).split(/[/?#:]/)[0];
    // A scheme link counts 23 whatever its length; a bare domain counts 23 or its own length, whichever is more, in case X does not link it.
    const linked = !isDomain(host) ? null : scheme ? 23 : Math.max(23, weigh(core));
    return linked === null ? weigh(run) : weigh(run.slice(0, from)) + linked + weigh(run.slice(to));
}

/** The weight of one word: its runs of ASCII URL characters and what lies between them. */
function weighWord(word: string): number {
    let weight = 0, last = 0, schemes = 0;
    // A keycap's digit, # or * stays with its emoji.
    for (const match of word.matchAll(/(?:[A-Za-z0-9\-._~:/?#[\]@!$&'()*+,;=%](?![\ufe0e\ufe0f\u20e3]))+/g)) {
        weight += weigh(word.slice(last, match.index));
        // X links a scheme only after a character that cannot be part of a word (see:https://… but not seehttps://…).
        for (const run of match[0].split(/(?<=^|[^A-Za-z0-9@$#])(?=https?:\/\/)/i)) {
            weight += weighRun(run);
            if (/^https?:\/\//i.test(run)) schemes++;
            if (weight > maxText) return weight;
        }
        last = match.index + match[0].length;
    }
    weight += weigh(word.slice(last));
    // A link whose host is not plain ASCII (https://例え.jp) or a domain joined to other letters still counts at least 23.
    return Math.max(weight, 23 * schemes, /(?=\p{L})[^\0-\x7f]\.[a-z]{2,}/iu.test(word) ? 23 : 0);
}

/**
 * The length X counts for a post, exact up to 280 and only "more than 280" beyond it, so that counting stops early
 * on long input. Text is NFC-normalized; code points in X's light ranges (Latin, most European scripts, common
 * punctuation) count 1 and others 2; an emoji counts 2; a link counts 23 and ends at the first character that is not
 * an ASCII URL character. Every shortcut is taken only when X would take it, so the count may exceed X's but should
 * not fall short of it; if it ever does, X refuses the post at dispatch with nothing posted.
 */
export function xLength(text: string): number {
    let total = 0;
    for (const word of text.normalize('NFC').split(/(\s+)/)) {
        total += /^\s/.test(word) ? weigh(word) : weighWord(word);
        if (total > maxText) return total;
    }
    return total;
}

export const x: PublishingAdapter = {
    name: 'x',
    contractRevision: 'x-post-v1',
    rules: {
        text: {max: maxText, unit: 'weighted characters', weigh: xLength},
        media: {max: 4, types: {'image/jpeg': maxImageBytes, 'image/png': maxImageBytes, 'image/webp': maxImageBytes}, maxAlt: maxAltText},
        schedule: null,
        note: 'X counts most CJK characters and every emoji as 2 and every link as 23.'
    },
    connector: {
        label: 'X', replaces: false,
        fields: [
            {name: 'apiKey', label: 'API key', secret: true, pattern: '[A-Za-z0-9]{10,64}'},
            {name: 'apiKeySecret', label: 'API key secret', secret: true, pattern: '[A-Za-z0-9]{30,80}'},
            {name: 'accessToken', label: 'Access token', secret: true, pattern: '[0-9]{1,20}-[A-Za-z0-9]{10,64}'},
            {name: 'accessTokenSecret', label: 'Access token secret', secret: true, pattern: '[A-Za-z0-9]{30,80}'}],
        help: 'In the X developer portal, give your app Read and Write permission, then copy its API key and secret and generate an access token and secret for your own account. Posting uses your app\'s X API access.',
        verify
    },
    async publish({env, accountId, secret, post}) {
        let keys: XKeys | null = null;
        try { keys = xKeys(JSON.parse(secret) as Record<string, unknown>); } catch { /* an unreadable secret is refused below */ }
        if (!keys) return {status: 'rejected', reason: 'credential-refused'};
        // Uploads come first and publish nothing, so any failure here is a refusal with nothing posted.
        const mediaIds: string[] = [];
        for (const file of post.media) {
            const object = await openMedia(env, accountId, file.mediaId);
            if (!object || object.size !== file.bytes) { void object?.body.cancel().catch(() => {}); return {status: 'rejected', reason: 'media-unavailable'}; }
            const form = new FormData();
            form.set('media', new Blob([await new Response(object.body).arrayBuffer()], {type: file.contentType}), 'media');
            form.set('media_category', 'tweet_image');
            let id: unknown = null;
            try {
                const response = await call(keys, 'POST', 'media/upload', {form, timeout: uploadTimeoutMs});
                id = data(response.ok ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null))?.id;
            } catch { /* no post exists yet */ }
            if (typeof id !== 'string' || !idPattern.test(id)) return {status: 'rejected', reason: 'media-upload-refused'};
            // Approved alt text is set before anything is posted; if X does not confirm it, nothing is posted rather than
            // a post without the alt text the owner approved. An empty alt text describes nothing, so it is not sent.
            if (file.alt) {
                let confirmed: unknown = null;
                try {
                    const response = await call(keys, 'POST', 'media/metadata', {json: {id, metadata: {alt_text: {text: file.alt}}}});
                    confirmed = data(response.status === 200 ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null))?.id;
                } catch { /* no post exists yet */ }
                if (confirmed !== id) return {status: 'rejected', reason: 'media-alt-refused'};
            }
            mediaIds.push(id);
        }
        const body = {...(post.text ? {text: post.text} : {}), ...(mediaIds.length ? {media: {media_ids: mediaIds}} : {})};
        // From here a post may exist. Only a definite refusal is a rejection; anything else is unknown and never re-sent.
        const response = await call(keys, 'POST', 'tweets', {json: body});
        if ([400, 401, 403, 429].includes(response.status)) {
            void response.body?.cancel().catch(() => {});
            return {status: 'rejected', reason: 'x-refused-' + response.status};
        }
        const created = data(response.status === 201 ? await boundedJson(response) : null);
        if (typeof created?.id !== 'string' || !idPattern.test(created.id)) throw Error('Unconfirmed X response.');
        return {status: 'completed', receipt: created.id};
    }
};
