/**
 * Discord as a publishing adapter through a channel webhook, which the owner creates in the channel's settings
 * (Integrations, Webhooks). The webhook URL carries its token, so it is the sealed secret; a request URL is never
 * logged or returned. Requests always go to the fixed discord.com API, never to a configured host.
 */
import { openMedia } from './external-media.ts';
import type { PublishingAdapter } from './publishing.ts';

const api = 'https://discord.com/api/v10/webhooks/';
const timeoutMs = 15_000, uploadTimeoutMs = 120_000, maxJsonBytes = 64 * 1024;
/** Discord's upload limit without server boosts is 10 MiB per message; files are read into memory before sending. */
const maxUploadBytes = 10 * 1024 * 1024;
const webhookPattern = /^https:\/\/(?:(?:canary|ptb)\.)?discord(?:app)?\.com\/api\/(?:v\d+\/)?webhooks\/([0-9]{17,20})\/([A-Za-z0-9_-]{60,100})$/;
/** No mention in an approved post may ping anyone: @everyone, @here, roles and users stay plain text. */
const noPings = {parse: []};

/** The webhook's id and token, or null when the text is not a Discord webhook URL. */
function webhook(url: string): {id: string; token: string} | null {
    const match = webhookPattern.exec(url.trim());
    return match ? {id: match[1], token: match[2]} : null;
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

/** The incoming webhook as Discord reports it now; null when Discord refuses it or does not answer. Reading it posts nothing. */
async function readWebhook(hook: {id: string; token: string}): Promise<{channelId: string; name: string} | null> {
    try {
        const response = await fetch(api + hook.id + '/' + hook.token, {redirect: 'manual', signal: AbortSignal.timeout(timeoutMs)});
        const found = response.status === 200 ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null);
        // Only an incoming webhook (type 1) posts messages; channel-follower and application webhooks do not.
        if (!found || found.id !== hook.id || found.type !== 1 || typeof found.channel_id !== 'string' || !/^[0-9]{17,20}$/.test(found.channel_id)) return null;
        return {channelId: found.channel_id, name: typeof found.name === 'string' && found.name ? found.name.slice(0, 80) : 'Webhook'};
    } catch { return null; }
}

/** The channel this webhook posts to; null when Discord refuses it or the text is not a webhook URL. */
async function verify(url: string) {
    const hook = webhook(url);
    const found = hook && await readWebhook(hook);
    return found && [{ref: hook!.id, targetAccount: found.channelId, targetDisplay: `${found.name} (channel ${found.channelId})`}];
}

export const discord: PublishingAdapter = {
    name: 'discord',
    contractRevision: 'discord-post-v1',
    rules: {
        text: {max: 2000, unit: 'characters'},
        media: {max: 10, types: {'image/jpeg': maxUploadBytes, 'image/png': maxUploadBytes, 'image/gif': maxUploadBytes,
            'image/webp': maxUploadBytes, 'video/mp4': maxUploadBytes}, maxTotalBytes: maxUploadBytes, maxAlt: 1024},
        schedule: null,
        note: 'Text is Discord markdown; mentions such as @everyone never ping anyone.'
    },
    connector: {
        label: 'Discord', replaces: false,
        fields: [{name: 'webhookUrl', label: 'Webhook URL', secret: true, placeholder: 'https://discord.com/api/webhooks/…',
            pattern: 'https://(canary\\.|ptb\\.)?discord(app)?\\.com/api/(v[0-9]+/)?webhooks/[0-9]{17,20}/[A-Za-z0-9_\\-]{60,100}'}],
        help: 'In Discord, open the channel settings, Integrations, Webhooks, create a webhook and copy its URL. Anyone with the URL can post to that channel.',
        verify: async ({webhookUrl}) => typeof webhookUrl === 'string' ? verify(webhookUrl) : null
    },
    async publish({env, accountId, targetAccount: channelId, secret, post}) {
        const hook = webhook(secret);
        if (!hook) return {status: 'rejected', reason: 'credential-refused'};
        // A server administrator can move a webhook to another channel. The approved channel is checked again before
        // anything is sent, so a moved, deleted or unreadable webhook posts nothing and needs reconnecting.
        const current = await readWebhook(hook);
        if (!current) return {status: 'rejected', reason: 'credential-refused'};
        if (current.channelId !== channelId) return {status: 'rejected', reason: 'webhook-moved'};
        // Each file is read in full before anything is sent, so a missing file refuses with nothing posted.
        const files: Blob[] = [];
        for (const file of post.media) {
            const object = await openMedia(env, accountId, file.mediaId);
            if (!object || object.size !== file.bytes) { void object?.body.cancel().catch(() => {}); return {status: 'rejected', reason: 'media-unavailable'}; }
            files.push(new Blob([await new Response(object.body).arrayBuffer()], {type: file.contentType}));
        }
        const payload = {content: post.text, allowed_mentions: noPings,
            ...(files.length ? {attachments: post.media.map((file, id) => ({id, filename: 'media' + id + extension(file.contentType),
                ...(file.alt ? {description: file.alt} : {})}))} : {})};
        const form = new FormData();
        form.set('payload_json', JSON.stringify(payload));
        files.forEach((blob, id) => form.set(`files[${id}]`, blob, 'media' + id + extension(blob.type)));
        // From here a message may be posted. Only a definite refusal is a rejection; anything else is unknown and never re-sent.
        const response = await fetch(api + hook.id + '/' + hook.token + '?wait=true', {method: 'POST', body: form, redirect: 'manual',
            signal: AbortSignal.timeout(files.length ? uploadTimeoutMs : timeoutMs)});
        if ([400, 401, 403, 404, 413, 429].includes(response.status)) {
            void response.body?.cancel().catch(() => {});
            return {status: 'rejected', reason: 'discord-refused-' + response.status};
        }
        const message = response.status === 200 ? await boundedJson(response) : null;
        if (typeof message?.id !== 'string' || !/^[0-9]{17,20}$/.test(message.id) || message.channel_id !== channelId) throw Error('Unconfirmed Discord response.');
        return {status: 'completed', receipt: channelId + '/' + message.id};
    }
};

const extension = (type: string) => ({'image/jpeg': '.jpg', 'image/png': '.png', 'image/gif': '.gif', 'image/webp': '.webp', 'video/mp4': '.mp4'})[type] ?? '';
