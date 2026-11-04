/**
 * Postiz Cloud as a publishing provider: one credential per Postiz channel ("integration"), authorized by the owner's
 * Postiz API key. Text, images and video go out through Postiz's public API; Weave keeps approval and the dispatch record.
 * Endpoints are fixed, never configuration. The API key stays in these functions and the vault.
 */
import type { Env } from './database.ts';
import type { ExternalProvider, PublicationState } from './external-providers.ts';
import { replaceInvisible } from './visible-text.ts';
import { readMediaRecord, signedMediaUrl } from './external-media.ts';

const api = 'https://api.postiz.com/public/v1';
const timeoutMs = 15_000, mediaTimeoutMs = 5 * 60_000, maxJsonBytes = 256 * 1024;
const maxText = 7000, maxMedia = 10, maxScheduleMs = 90 * 24 * 3600_000;

/** Channels this adapter supports and the settings Postiz requires for each (docs.postiz.com, public-api/posts/create). */
export const postizChannels: Readonly<Record<string, readonly string[]>> = {
    x: ['who_can_reply_post'], linkedin: [], 'linkedin-page': [], facebook: [], instagram: ['post_type'], 'instagram-standalone': ['post_type'],
    threads: [], bluesky: [], mastodon: [], telegram: [], youtube: ['title', 'type'], pinterest: ['board'], reddit: ['subreddit'], discord: ['channel'],
    tiktok: ['privacy_level', 'duet', 'stitch', 'comment', 'autoAddMusic', 'brand_content_toggle', 'brand_organic_toggle', 'content_posting_method'],
};
/** Postiz accepts these media types; others are refused before approval rather than failing at Postiz. */
const postizMedia = new Set(['image/jpeg', 'image/png', 'image/gif', 'image/webp', 'video/mp4']);
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/, sha = /^[0-9a-f]{64}$/;
const integrationId = /^[A-Za-z0-9_-]{1,64}$/;

export type PostizChannel = {targetAccount: string; targetDisplay: string};
export type PostPlan = {text: string; media: {mediaId: string; bytes: number; sha256: string}[]; settings: Record<string, unknown>; publishAt?: string};

async function boundedJson(response: Response): Promise<unknown> {
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
        return JSON.parse(new TextDecoder('utf-8', {fatal: true}).decode(all)) as unknown;
    } catch { return null; }
    finally { void reader.cancel().catch(() => {}); }
}
const call = (path: string, apiKey: string, init: RequestInit & {timeout?: number} = {}) => fetch(api + path, {...init, redirect: 'manual',
    signal: AbortSignal.timeout(init.timeout ?? timeoutMs), headers: {Authorization: apiKey, Accept: 'application/json', ...(init.body ? {'Content-Type': 'application/json'} : {}), ...init.headers}});

/** The key's enabled, supported channels; null when Postiz refuses the key. `targetAccount` is "<identifier>/<integration id>". */
export async function listPostizChannels(apiKey: string): Promise<PostizChannel[] | null> {
    try {
        const response = await call('/integrations', apiKey);
        const list = response.ok ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null);
        if (!Array.isArray(list)) return null;
        const channels: PostizChannel[] = [];
        for (const item of list as Record<string, unknown>[]) {
            const {id, identifier, name, profile, disabled} = item ?? {};
            if (disabled === true || typeof id !== 'string' || !integrationId.test(id) || typeof identifier !== 'string' || !Object.hasOwn(postizChannels, identifier)) continue;
            const label = typeof name === 'string' ? name.slice(0, 120) : id;
            channels.push({targetAccount: identifier + '/' + id, targetDisplay: identifier + ' · ' + label + (typeof profile === 'string' && profile ? ' (@' + profile.slice(0, 80) + ')' : '')});
        }
        return channels;
    } catch { return null; }
}

/**
 * The content of a publish_post action, or null when it is not exactly this shape. With `now`, a publishAt must also be in
 * the future within the scheduling horizon; dispatch passes null and checks only that the approved time has not passed.
 */
export function postPlan(content: string, now: number | null): PostPlan | null {
    let value: unknown;
    try { value = JSON.parse(content); } catch { return null; }
    const plan = value as Record<string, unknown>;
    if (!plan || typeof plan !== 'object' || Array.isArray(plan)) return null;
    const keys = Object.keys(plan).sort().join();
    if (keys !== 'media,settings,text' && keys !== 'media,publishAt,settings,text') return null;
    const {text, media, settings, publishAt} = plan;
    if (typeof text !== 'string' || text.length > maxText || !Array.isArray(media) || media.length > maxMedia) return null;
    if (text.trim() === '' && media.length === 0) return null;
    if (!settings || typeof settings !== 'object' || Array.isArray(settings) || typeof (settings as {__type?: unknown}).__type !== 'string') return null;
    const files: PostPlan['media'] = [];
    for (const file of media as Record<string, unknown>[]) {
        if (!file || typeof file !== 'object' || Object.keys(file).sort().join() !== 'bytes,mediaId,sha256') return null;
        if (typeof file.mediaId !== 'string' || !uuid.test(file.mediaId) || typeof file.sha256 !== 'string' || !sha.test(file.sha256)
            || typeof file.bytes !== 'number' || !Number.isSafeInteger(file.bytes) || file.bytes < 1) return null;
        files.push({mediaId: file.mediaId, bytes: file.bytes, sha256: file.sha256});
    }
    if (publishAt !== undefined) {
        // An exact UTC instant in the future, within Postiz's scheduling horizon.
        const at = typeof publishAt === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z$/.test(publishAt) ? Date.parse(publishAt) : NaN;
        if (Number.isNaN(at) || (now !== null && !(at > now && at <= now + maxScheduleMs))) return null;
    }
    return {text, media: files, settings: settings as Record<string, unknown>, ...(publishAt === undefined ? {} : {publishAt: publishAt as string})};
}

export const postizPublish: ExternalProvider & {available(env: Env): boolean} = {
    name: 'postiz',
    contractRevision: 'postiz-post-v1',
    operations: ['publish_post'],
    available: env => !!env.EXTERNAL_CREDENTIAL_KEY,
    describeContent(targetAccount) {
        const identifier = targetAccount.split('/')[0], required = postizChannels[identifier] ?? [];
        return `JSON {"text": at most ${maxText} characters, "media": up to ${maxMedia} of your uploaded JPEG, PNG, GIF, WebP or MP4 files as {"mediaId", "bytes", "sha256"}, `
            + `"settings": {"__type": "${identifier}"${required.map(key => `, "${key}"`).join('')}} with values as Postiz documents them for ${identifier}, `
            + `"publishAt": optional future UTC time such as 2026-01-31T09:00:00Z, at most 90 days ahead}. Text or media is required.`;
    },
    async checkContent(tx, {accountId, content, targetAccount}) {
        const plan = postPlan(content, Date.now());
        if (!plan) return 'must be JSON {text, media, settings, publishAt?}: text or media, at most 10 media, settings.__type, a future UTC publishAt';
        const identifier = targetAccount.split('/')[0];
        if (plan.settings.__type !== identifier) return `settings.__type must be "${identifier}" for this channel`;
        const missing = (postizChannels[identifier] ?? []).filter(key => !Object.hasOwn(plan.settings, key));
        if (missing.length) return 'settings must include ' + missing.join(', ');
        for (const file of plan.media) {
            const media = await readMediaRecord(tx, accountId, file.mediaId);
            // Each file is pinned to the exact uploaded bytes, so the owner approves these files and no others.
            if (!media || media.bytes !== file.bytes || media.sha256 !== file.sha256) return 'media must name your uploaded files with their exact bytes and sha256';
            if (!postizMedia.has(media.contentType)) return 'media must be JPEG, PNG, GIF, WebP or MP4 for Postiz';
        }
        return null;
    },
    async execute(env, call_) {
        const plan = postPlan(call_.content, null);
        if (!plan || call_.secret === null) return {status: 'rejected', reason: 'plan-unreadable'};
        // An approved schedule that has already passed is not silently turned into an immediate post.
        if (plan.publishAt && Date.parse(plan.publishAt) <= Date.now()) return {status: 'rejected', reason: 'schedule-passed'};
        const apiKey = call_.secret, integration = call_.targetAccount.split('/')[1];
        // Media is copied into Postiz first. Uploaded media alone publishes nothing, so any failure here is a clean refusal.
        const images: {id: string; path: string}[] = [];
        for (const file of plan.media) {
            try {
                const url = await signedMediaUrl(env, call_.accountId, file.mediaId, Date.now());
                const response = await call('/upload-from-url', apiKey, {method: 'POST', body: JSON.stringify({url}), timeout: mediaTimeoutMs});
                const uploaded = response.ok ? await boundedJson(response) as {id?: unknown; path?: unknown} | null : (void response.body?.cancel().catch(() => {}), null);
                if (typeof uploaded?.id !== 'string' || typeof uploaded.path !== 'string' || !uploaded.path.startsWith('https://')) return {status: 'rejected', reason: 'media-upload-refused'};
                images.push({id: uploaded.id, path: uploaded.path});
            } catch { return {status: 'rejected', reason: 'media-upload-refused'}; }
        }
        const body = {type: plan.publishAt ? 'schedule' : 'now', date: plan.publishAt ?? new Date().toISOString(), shortLink: false, tags: [],
            posts: [{integration: {id: integration}, value: [{content: plan.text, image: images}], settings: plan.settings}]};
        // From here a post may be created. Only a definite refusal is a rejection; everything else is unknown and never re-sent.
        const response = await call('/posts', apiKey, {method: 'POST', body: JSON.stringify(body)});
        if ([400, 401, 403, 422, 429].includes(response.status)) {
            void response.body?.cancel().catch(() => {});
            return {status: 'rejected', reason: 'postiz-refused-' + response.status};
        }
        const created = response.ok ? await boundedJson(response) : null;
        const postId = Array.isArray(created) && created.length === 1 ? (created[0] as {postId?: unknown}).postId : null;
        if (typeof postId !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(postId)) throw Error('Unconfirmed Postiz response.');
        // Postiz queues even an immediate post; either way the platform's answer comes later.
        return {status: 'completed', receipt: (plan.publishAt ? 'scheduled:' : 'posted:') + postId,
            trackFrom: Math.max(Date.now(), plan.publishAt ? Date.parse(plan.publishAt) : 0) + trackDelayMs};
    },
    async publicationStates(apiKey, items) {
        // One listing covers every item: Postiz offers no read by ID, only a date range (and 30 calls per hour).
        const from = Math.min(...items.map(item => item.trackFrom)) - trackDelayMs - listMarginMs;
        const to = Math.max(...items.map(item => item.trackFrom)) + listMarginMs;
        const query = new URLSearchParams({startDate: new Date(from).toISOString(), endDate: new Date(to).toISOString()});
        const response = await call('/posts?' + query, apiKey);
        const listed = response.ok ? await boundedJson(response) as {posts?: unknown} | null : (void response.body?.cancel().catch(() => {}), null);
        if (!listed || !Array.isArray(listed.posts)) throw Error('Postiz did not list posts.');
        const states = new Map<string, PublicationState>();
        for (const post of listed.posts as Record<string, unknown>[]) {
            if (typeof post?.id !== 'string' || !postIdPattern.test(post.id)) continue;
            const state = publicationState(post);
            for (const prefix of ['posted:', 'scheduled:']) states.set(prefix + post.id, state);
        }
        return new Map(items.filter(item => states.has(item.receipt)).map(item => [item.receipt, states.get(item.receipt)!]));
    }
};

const trackDelayMs = 2 * 60_000, listMarginMs = 24 * 3600_000, maxError = 300;
const postIdPattern = /^[A-Za-z0-9_-]{1,64}$/;
/** Postiz's QUEUE and DRAFT are not final; PUBLISHED carries the post URL and ERROR the platform's message. */
function publicationState(post: Record<string, unknown>): PublicationState {
    if (post.state === 'PUBLISHED') {
        const url = typeof post.releaseURL === 'string' && post.releaseURL.length <= 2048 && URL.canParse(post.releaseURL)
            && new URL(post.releaseURL).protocol === 'https:' ? post.releaseURL : null;
        return {state: 'published', url, error: null};
    }
    if (post.state === 'ERROR') {
        const text = typeof post.error === 'string' ? replaceInvisible(post.error, ' ').replace(/\s+/g, ' ').trim().slice(0, maxError) : '';
        return {state: 'failed', url: null, error: text || null};
    }
    return {state: 'queued', url: null, error: null};
}
