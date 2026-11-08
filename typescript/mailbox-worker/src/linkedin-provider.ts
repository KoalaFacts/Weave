/**
 * LinkedIn as a native publishing adapter: posts on the owner's own member feed through LinkedIn's versioned Posts and
 * Images APIs. The owner creates an app in the LinkedIn developer portal with "Sign In with LinkedIn using OpenID
 * Connect" and "Share on LinkedIn", generates a member access token with the openid, profile and w_member_social scopes
 * and pastes it; it is sealed and never logged or returned. Endpoints are fixed, never configuration.
 */
import { openMedia } from './external-media.ts';
import type { PublishingAdapter } from './publishing.ts';

const api = 'https://api.linkedin.com/';
/** The Marketing API version every call names; LinkedIn sunsets a version about a year after its release. */
export const linkedinVersion = '202610';
const timeoutMs = 15_000, uploadTimeoutMs = 120_000, maxJsonBytes = 64 * 1024;
/** Files are read into memory one at a time before upload, so each image is kept small. */
const maxImageBytes = 8_000_000;
/** LinkedIn's limit for a post's commentary, counted after reserved characters are escaped. */
const maxText = 3000;
/** LinkedIn's tokens are about 500 characters, and LinkedIn asks apps to handle at least 1000; the router takes up to 2048. */
const tokenPattern = /^[A-Za-z0-9\-._~+/]{20,2000}={0,2}$/, memberPattern = /^[A-Za-z0-9_-]{1,64}$/;
const postPattern = /^urn:li:(?:share|ugcPost):[0-9]{1,30}$/, imagePattern = /^urn:li:image:[A-Za-z0-9_-]{1,100}$/;

/** Little text format: these characters are markup unless escaped with a backslash, even where no markup uses them. */
const reserved = /[|{}@[\]()<>#\\*_~]/g;
export const littleText = (text: string) => text.replace(reserved, char => '\\' + char);
/** The length LinkedIn counts: the commentary as sent, so each reserved character counts 2. */
export const linkedinLength = (text: string) => text.length + (text.match(reserved)?.length ?? 0);

const token = (value: unknown) => typeof value === 'string' && tokenPattern.test(value.trim()) ? value.trim() : null;

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
const drop = (response: Response) => void response.body?.cancel().catch(() => {});

/** One LinkedIn API call with the member's token. Network failures throw; the caller classifies the status. */
function call(accessToken: string, method: string, path: string, json?: unknown) {
    const headers: Record<string, string> = {Authorization: 'Bearer ' + accessToken, 'LinkedIn-Version': linkedinVersion, 'X-Restli-Protocol-Version': '2.0.0'};
    if (json !== undefined) headers['Content-Type'] = 'application/json';
    return fetch(api + path, {method, headers, redirect: 'manual', signal: AbortSignal.timeout(timeoutMs), ...(json !== undefined ? {body: JSON.stringify(json)} : {})});
}

/** The member the token acts as; null when LinkedIn refuses it or the value is not a token. Reading it posts nothing. */
async function verify(values: Readonly<Record<string, string>>) {
    const accessToken = token(values.accessToken);
    if (!accessToken) return null;
    try {
        const response = await fetch(api + 'v2/userinfo', {headers: {Authorization: 'Bearer ' + accessToken}, redirect: 'manual', signal: AbortSignal.timeout(timeoutMs)});
        const user = response.status === 200 ? await boundedJson(response) : (drop(response), null);
        if (typeof user?.sub !== 'string' || !memberPattern.test(user.sub)) return null;
        const name = typeof user.name === 'string' && user.name.trim() ? user.name.trim().slice(0, 100) : 'LinkedIn member';
        return [{ref: user.sub, targetAccount: 'urn:li:person:' + user.sub, targetDisplay: name}];
    } catch { return null; }
}

/**
 * True only for an upload address on LinkedIn's own HTTPS hosts (the docs show www.linkedin.com), so the token is never
 * sent anywhere else. An address on any other host is refused, even one LinkedIn might start using.
 */
function linkedinUpload(value: unknown): URL | null {
    if (typeof value !== 'string') return null;
    try {
        const url = new URL(value);
        return url.protocol === 'https:' && url.port === '' && !url.username && !url.password
            && (url.hostname === 'linkedin.com' || url.hostname.endsWith('.linkedin.com')) ? url : null;
    } catch { return null; }
}

export const linkedin: PublishingAdapter = {
    name: 'linkedin',
    contractRevision: 'linkedin-post-v1',
    rules: {
        text: {max: maxText, unit: 'weighted characters', weigh: linkedinLength},
        media: {max: 9, types: {'image/jpeg': maxImageBytes, 'image/png': maxImageBytes, 'image/gif': maxImageBytes}},
        schedule: null,
        note: 'LinkedIn counts each of | { } @ [ ] ( ) < > # \\ * _ ~ as 2, because they are sent escaped; they appear as written, never as mentions or hashtags. Posts are public on the member\'s feed.'
    },
    connector: {
        label: 'LinkedIn', replaces: false,
        fields: [{name: 'accessToken', label: 'Access token', secret: true, pattern: '[A-Za-z0-9\\-._~+/]{20,2000}={0,2}'}],
        help: 'In the LinkedIn developer portal, add "Sign In with LinkedIn using OpenID Connect" and "Share on LinkedIn" to your app, then generate a member access token with the openid, profile and w_member_social scopes and paste it here. LinkedIn tokens expire after about 60 days; connect again with a new one.',
        verify
    },
    async publish({env, accountId, targetAccount, secret, post}) {
        const accessToken = token(secret);
        if (!accessToken) return {status: 'rejected', reason: 'credential-refused'};
        // Uploads come first and publish nothing, so any failure here is a refusal with nothing posted.
        const images: {id: string; altText?: string}[] = [];
        for (const file of post.media) {
            let image: unknown = null, uploadUrl: URL | null = null;
            try {
                const response = await call(accessToken, 'POST', 'rest/images?action=initializeUpload', {initializeUploadRequest: {owner: targetAccount}});
                const value = (response.status === 200 ? await boundedJson(response) : (drop(response), null))?.value as Record<string, unknown> | undefined;
                image = value?.image; uploadUrl = linkedinUpload(value?.uploadUrl);
            } catch { /* no post exists yet */ }
            if (typeof image !== 'string' || !imagePattern.test(image) || !uploadUrl) return {status: 'rejected', reason: 'media-upload-refused'};
            const object = await openMedia(env, accountId, file.mediaId);
            if (!object || object.size !== file.bytes) { void object?.body.cancel().catch(() => {}); return {status: 'rejected', reason: 'media-unavailable'}; }
            let uploaded = false;
            try {
                const response = await fetch(uploadUrl, {method: 'PUT', headers: {Authorization: 'Bearer ' + accessToken, 'Content-Type': file.contentType},
                    body: await new Response(object.body).arrayBuffer(), redirect: 'manual', signal: AbortSignal.timeout(uploadTimeoutMs)});
                drop(response);
                uploaded = response.status === 200 || response.status === 201;
            } catch { /* no post exists yet */ }
            if (!uploaded) return {status: 'rejected', reason: 'media-upload-refused'};
            // An empty alt text describes nothing, so it is not sent.
            images.push({id: image, ...(file.alt ? {altText: file.alt} : {})});
        }
        const content = images.length === 1 ? {content: {media: images[0]}} : images.length ? {content: {multiImage: {images}}} : {};
        const body = {author: targetAccount, commentary: littleText(post.text), visibility: 'PUBLIC',
            distribution: {feedDistribution: 'MAIN_FEED', targetEntities: [], thirdPartyDistributionChannels: []},
            lifecycleState: 'PUBLISHED', isReshareDisabledByAuthor: false, ...content};
        // From here a post may exist. Only a definite refusal is a rejection; anything else is unknown and never re-sent.
        const response = await call(accessToken, 'POST', 'rest/posts', body);
        drop(response);
        // 426 is LinkedIn refusing a sunset API version before it reads the post.
        if ([400, 401, 403, 422, 426, 429].includes(response.status)) return {status: 'rejected', reason: 'linkedin-refused-' + response.status};
        const id = response.status === 201 ? response.headers.get('x-restli-id') : null;
        if (!id || !postPattern.test(id)) throw Error('Unconfirmed LinkedIn response.');
        return {status: 'completed', receipt: id};
    }
};
