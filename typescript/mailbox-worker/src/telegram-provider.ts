/**
 * Telegram as a publishing adapter: a bot the owner created with @BotFather posts to a channel or group in which it is an
 * administrator. The Bot API carries the token in its URL path, so a request URL is never logged or returned. The endpoint
 * is fixed, never configuration. The token stays in these functions and the vault.
 */
import { openMedia } from './external-media.ts';
import type { PublishingAdapter } from './publishing.ts';

const api = 'https://api.telegram.org/bot';
const timeoutMs = 15_000, uploadTimeoutMs = 120_000, maxJsonBytes = 64 * 1024;
/** Bot API uploads: photos up to 10 MB, other files up to 50 MB. Files are read into memory, so one post stays within 50 MB. */
const maxPhotoBytes = 10_000_000, maxVideoBytes = 50_000_000;
const botTokenPattern = /^[0-9]{5,15}:[A-Za-z0-9_-]{30,64}$/;
const chatPattern = /^(?:@[A-Za-z][A-Za-z0-9_]{3,31}|-?[0-9]{1,20})$/;
type Answer = {ok: true; result: unknown} | {ok: false; status: number};

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

/** One Bot API call. Network failures throw; any HTTP answer is returned with its status so callers can classify it. */
async function call(token: string, method: string, body: Record<string, unknown> | FormData, timeout = timeoutMs): Promise<Answer> {
    const response = await fetch(api + token + '/' + method, {method: 'POST', redirect: 'manual', signal: AbortSignal.timeout(timeout),
        ...(body instanceof FormData ? {body} : {headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body)})});
    const answer = await boundedJson(response);
    return response.status === 200 && answer?.ok === true ? {ok: true, result: answer.result} : {ok: false, status: response.status};
}

/** The chat a bot administers and may post in; null when Telegram refuses or the inputs are not a bot token and a chat. */
async function verify(botToken: string, chat: string) {
    const chatRef = chat.trim();
    if (!botTokenPattern.test(botToken) || !chatPattern.test(chatRef)) return null;
    try {
        const me = await call(botToken, 'getMe', {});
        const bot = me.ok ? me.result as {id?: unknown; is_bot?: unknown} : null;
        if (!bot || typeof bot.id !== 'number' || bot.is_bot !== true) return null;
        const found = await call(botToken, 'getChat', {chat_id: chatRef});
        const info = found.ok ? found.result as {id?: unknown; type?: unknown; title?: unknown; username?: unknown} : null;
        if (!info || typeof info.id !== 'number' || !['channel', 'group', 'supergroup'].includes(info.type as string)) return null;
        const member = await call(botToken, 'getChatMember', {chat_id: info.id, user_id: bot.id});
        const role = member.ok ? member.result as {status?: unknown; can_post_messages?: unknown} : null;
        if (!role || (role.status !== 'administrator' && role.status !== 'creator')) return null;
        // In a channel only an administrator with this right can post.
        if (info.type === 'channel' && role.status === 'administrator' && role.can_post_messages !== true) return null;
        const title = typeof info.title === 'string' ? info.title.slice(0, 120) : String(info.id);
        const display = title + (typeof info.username === 'string' ? ' (@' + info.username.slice(0, 32) + ')' : '');
        return [{ref: String(info.id), targetAccount: String(info.id), targetDisplay: display}];
    } catch { return null; }
}

export const telegram: PublishingAdapter = {
    name: 'telegram',
    contractRevision: 'telegram-post-v1',
    rules: {
        text: {max: 4096, unit: 'characters', maxWithMedia: 1024},
        media: {max: 10, types: {'image/jpeg': maxPhotoBytes, 'image/png': maxPhotoBytes, 'video/mp4': maxVideoBytes}, maxTotalBytes: maxVideoBytes},
        schedule: null,
        note: 'Text is sent as plain text; two or more files are posted as one album.'
    },
    connector: {
        label: 'Telegram', replaces: false,
        fields: [{name: 'botToken', label: 'Bot token', secret: true, pattern: '[0-9]{5,15}:[A-Za-z0-9_\\-]{30,64}'},
            {name: 'chat', label: 'Channel or group', secret: false, placeholder: '@yourchannel'}],
        help: 'Create a bot with @BotFather, add it to your channel or group as an administrator allowed to post, then enter its token and the chat.',
        verify: async ({botToken, chat}) => typeof botToken === 'string' && typeof chat === 'string' ? verify(botToken, chat) : null
    },
    async publish({env, accountId, targetAccount: chatId, secret: token, post}) {
        // Each file is read in full before anything is sent, so a missing file refuses with nothing posted.
        const files: {kind: 'photo' | 'video'; blob: Blob}[] = [];
        for (const file of post.media) {
            const object = await openMedia(env, accountId, file.mediaId);
            if (!object || object.size !== file.bytes) { void object?.body.cancel().catch(() => {}); return {status: 'rejected', reason: 'media-unavailable'}; }
            files.push({kind: file.contentType === 'video/mp4' ? 'video' : 'photo', blob: new Blob([await new Response(object.body).arrayBuffer()], {type: file.contentType})});
        }
        // From here a message may be posted. Only a definite refusal is a rejection; anything else is unknown and never re-sent.
        let answer: Answer;
        if (files.length === 0) answer = await call(token, 'sendMessage', {chat_id: chatId, text: post.text});
        else {
            const form = new FormData();
            form.set('chat_id', chatId);
            if (files.length === 1) {
                form.set(files[0].kind, files[0].blob, 'media');
                if (post.text) form.set('caption', post.text);
                answer = await call(token, files[0].kind === 'photo' ? 'sendPhoto' : 'sendVideo', form, uploadTimeoutMs);
            } else {
                files.forEach((file, index) => form.set('file' + index, file.blob, 'media' + index));
                form.set('media', JSON.stringify(files.map((file, index) => ({type: file.kind, media: 'attach://file' + index, ...(index === 0 && post.text ? {caption: post.text} : {})}))));
                answer = await call(token, 'sendMediaGroup', form, uploadTimeoutMs);
            }
        }
        if (!answer.ok) {
            if ([400, 401, 403, 404, 413, 429].includes(answer.status)) return {status: 'rejected', reason: 'telegram-refused-' + answer.status};
            throw Error('Unconfirmed Telegram response.');
        }
        const messages = Array.isArray(answer.result) ? answer.result : [answer.result];
        const first = (messages[0] as {message_id?: unknown} | undefined)?.message_id;
        if (typeof first !== 'number' || messages.length !== Math.max(1, files.length)) throw Error('Unconfirmed Telegram response.');
        return {status: 'completed', receipt: chatId + '/' + first};
    }
};
