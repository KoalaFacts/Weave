/**
 * Postiz Cloud as a publishing adapter: one credential per Postiz channel ("integration"), authorized by the owner's
 * Postiz API key. Text, images and video go out through Postiz's public API; the router keeps approval and the dispatch
 * record. Endpoints are fixed, never configuration. The API key stays in these functions and the vault.
 */
import type { PublicationState } from './external-providers.ts';
import { visibleExcerpt } from './visible-text.ts';
import { maxMediaBytes, signedMediaUrl } from './external-media.ts';
import type { PublishingAdapter, SettingRules, TextRule } from './publishing.ts';
import { xLength } from './x-provider.ts';

const api = 'https://api.postiz.com/public/v1';
const timeoutMs = 15_000, mediaTimeoutMs = 5 * 60_000, maxJsonBytes = 256 * 1024;

/** Channels this adapter supports and the settings Postiz requires for each (docs.postiz.com, public-api/posts/create). */
export const postizChannels: Readonly<Record<string, readonly string[]>> = {
    x: ['who_can_reply_post'], linkedin: [], 'linkedin-page': [], facebook: [], instagram: ['post_type'], 'instagram-standalone': ['post_type'],
    threads: [], bluesky: [], mastodon: [], telegram: [], youtube: ['title', 'type'], pinterest: ['board'], reddit: ['subreddit'], discord: ['channel'],
    tiktok: ['privacy_level', 'duet', 'stitch', 'comment', 'autoAddMusic', 'brand_content_toggle', 'brand_organic_toggle', 'content_posting_method'],
};
/**
 * Postiz takes 7000 characters, but the platform behind a channel refuses longer text after the owner approved it. Where the
 * platform's limit is smaller and countable here, it is the channel's limit: X and Bluesky as their native adapters count,
 * LinkedIn's 3000 and Instagram's 2200 caption characters. Threads is left out: Meta counts an emoji as its UTF-8 bytes,
 * which none of these units does.
 */
const postizText: TextRule = {max: 7000, unit: 'characters'};
const channelText: Readonly<Record<string, TextRule>> = {
    x: {max: 280, unit: 'weighted characters', weigh: xLength}, bluesky: {max: 300, unit: 'graphemes', maxBytes: 3000},
    linkedin: {max: 3000, unit: 'characters'}, 'linkedin-page': {max: 3000, unit: 'characters'},
    instagram: {max: 2200, unit: 'characters'}, 'instagram-standalone': {max: 2200, unit: 'characters'},
};
/** Setting values Postiz documents as a closed list (docs.postiz.com/public-api/providers/x); other settings are checked for presence only. */
const channelSettings: Readonly<Record<string, SettingRules>> = {
    x: {who_can_reply_post: {type: 'string', values: ['everyone', 'following', 'mentionedUsers', 'subscribers', 'verified']}},
};
const integrationId = /^[A-Za-z0-9_-]{1,64}$/, apiKeyPattern = /^[\x21-\x7e]{16,512}$/;

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
async function listPostizChannels(apiKey: string) {
    try {
        const response = await call('/integrations', apiKey);
        const list = response.ok ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null);
        if (!Array.isArray(list)) return null;
        const channels: {ref: string; targetAccount: string; targetDisplay: string}[] = [];
        for (const item of list as Record<string, unknown>[]) {
            const {id, identifier, name, profile, disabled} = item ?? {};
            if (disabled === true || typeof id !== 'string' || !integrationId.test(id) || typeof identifier !== 'string' || !Object.hasOwn(postizChannels, identifier)) continue;
            const label = typeof name === 'string' ? name.slice(0, 120) : id;
            channels.push({ref: id, targetAccount: identifier + '/' + id,
                targetDisplay: identifier + ' · ' + label + (typeof profile === 'string' && profile ? ' (@' + profile.slice(0, 80) + ')' : '')});
        }
        return channels;
    } catch { return null; }
}

export const postiz: PublishingAdapter = {
    name: 'postiz',
    contractRevision: 'postiz-post-v1',
    rules: {
        text: targetAccount => {
            const identifier = targetAccount.split('/')[0];
            return Object.hasOwn(channelText, identifier) ? channelText[identifier] : postizText;
        },
        media: {max: 10, types: Object.fromEntries(['image/jpeg', 'image/png', 'image/gif', 'image/webp', 'video/mp4'].map(type => [type, maxMediaBytes])), maxAlt: 0},
        schedule: 90 * 24 * 3600_000,
        settings: targetAccount => {
            const identifier = targetAccount.split('/')[0];
            return {fixed: {__type: identifier}, required: postizChannels[identifier] ?? [], documented: `with values as Postiz documents them for ${identifier}`,
                rules: Object.hasOwn(channelSettings, identifier) ? channelSettings[identifier] : {}};
        }
    },
    connector: {
        label: 'Postiz', replaces: true,
        fields: [{name: 'apiKey', label: 'Postiz API key', secret: true}],
        help: 'One key connects every channel you added in Postiz; reconnecting with it again refreshes the list.',
        verify: async ({apiKey}) => typeof apiKey === 'string' && apiKeyPattern.test(apiKey) ? listPostizChannels(apiKey) : null
    },
    async publish({env, accountId, targetAccount, secret: apiKey, post}) {
        const integration = targetAccount.split('/')[1];
        // Media is copied into Postiz first. Uploaded media alone publishes nothing, so any failure here is a clean refusal.
        const images: {id: string; path: string}[] = [];
        for (const file of post.media) {
            try {
                const url = await signedMediaUrl(env, accountId, file.mediaId, Date.now());
                const response = await call('/upload-from-url', apiKey, {method: 'POST', body: JSON.stringify({url}), timeout: mediaTimeoutMs});
                const uploaded = response.ok ? await boundedJson(response) as {id?: unknown; path?: unknown} | null : (void response.body?.cancel().catch(() => {}), null);
                if (typeof uploaded?.id !== 'string' || typeof uploaded.path !== 'string' || !uploaded.path.startsWith('https://')) return {status: 'rejected', reason: 'media-upload-refused'};
                images.push({id: uploaded.id, path: uploaded.path});
            } catch { return {status: 'rejected', reason: 'media-upload-refused'}; }
        }
        const body = {type: post.publishAt ? 'schedule' : 'now', date: post.publishAt ?? new Date().toISOString(), shortLink: false, tags: [],
            posts: [{integration: {id: integration}, value: [{content: post.text, image: images}], settings: post.settings}]};
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
        return {status: 'completed', receipt: (post.publishAt ? 'scheduled:' : 'posted:') + postId,
            trackFrom: Math.max(Date.now(), post.publishAt ? Date.parse(post.publishAt) : 0) + trackDelayMs};
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
        const text = typeof post.error === 'string' ? visibleExcerpt(post.error, maxError) : '';
        return {state: 'failed', url: null, error: text || null};
    }
    return {state: 'queued', url: null, error: null};
}
