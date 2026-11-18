/**
 * Adapts the Slack standalone text and single-file send from Hermes Agent
 * https://github.com/NousResearch/hermes-agent/blob/4ae451d4126327c871fd084e60c045dec0c85911/plugins/platforms/slack/adapter.py#L6733
 *
 * MIT License
 * Copyright (c) 2025 Nous Research
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */
import { openMedia } from './external-media.ts';
import type { ProviderResult } from './external-providers.ts';
import type { PublishingAdapter, PublishCall } from './publishing.ts';

const api = 'https://slack.com/api/';
const tokenPattern = /^xoxb-[A-Za-z0-9-]{1,2043}$/;
const targetPattern = /^(T[A-Z0-9]{1,24}):([CG][A-Z0-9]{1,24})$/;
const slackTimestamp = /^[0-9]{1,16}\.[0-9]{6}$/;
const fileIdPattern = /^F[A-Z0-9]{1,24}$/;
const maxUploadBytes = 10 * 1024 * 1024;
const maxResponseBytes = 64 * 1024;
const extensions: Readonly<Record<string, string>> = {'image/jpeg': '.jpg', 'image/png': '.png', 'image/gif': '.gif', 'image/webp': '.webp'};
const refused = new Set(['invalid_auth', 'not_authed', 'token_revoked', 'account_inactive', 'missing_scope', 'channel_not_found',
    'not_in_channel', 'is_archived', 'restricted_action', 'invalid_arguments', 'msg_too_long', 'no_text', 'not_allowed_token_type']);
type Method = 'auth.test' | 'conversations.info' | 'chat.postMessage' | 'files.getUploadURLExternal' | 'files.completeUploadExternal';
type RecordValue = Record<string, unknown>;
const record = (value: unknown): RecordValue | null =>
    value !== null && typeof value === 'object' && !Array.isArray(value) ? value as RecordValue : null;
const drop = (response: Response) => { void response.body?.cancel().catch(() => {}); };
const rejected = (reason: string): ProviderResult => ({status: 'rejected', reason});

async function call(token: string, method: Method, body: RecordValue | URLSearchParams = {}, timeoutMs = 15_000) {
    const form = body instanceof URLSearchParams;
    const read = method === 'conversations.info';
    const response = await fetch(api + method + (read ? '?' + String(body) : ''), {method: read ? 'GET' : 'POST', redirect: 'manual', signal: AbortSignal.timeout(timeoutMs),
        headers: {authorization: 'Bearer ' + token, 'content-type': form ? 'application/x-www-form-urlencoded' : 'application/json'},
        body: read ? undefined : form ? body : JSON.stringify(body)});
    const status = response.status;
    const reader = status === 200 ? response.body?.getReader() : null;
    if (!reader) { drop(response); return {status, data: null}; }
    try {
        const decoder = new TextDecoder('utf-8', {fatal: true});
        let text = '', size = 0;
        for (;;) {
            const item = await reader.read();
            if (item.done) break;
            size += item.value.byteLength;
            if (size > maxResponseBytes) throw Error('Slack response exceeded the size limit.');
            text += decoder.decode(item.value, {stream: true});
        }
        return {status, data: record(JSON.parse(text + decoder.decode()))};
    } finally { void reader.cancel().catch(() => {}); }
}

async function verifiedChannel(token: string, channelId: string) {
    if (!tokenPattern.test(token) || !/^[CG][A-Z0-9]{1,24}$/.test(channelId)) return null;
    try {
        const auth = (await call(token, 'auth.test')).data;
        if (auth?.ok !== true || typeof auth.bot_id !== 'string' || !/^B[A-Z0-9]{1,24}$/.test(auth.bot_id)
            || typeof auth.team_id !== 'string' || !targetPattern.test(auth.team_id + ':' + channelId)) return null;
        const info = (await call(token, 'conversations.info', new URLSearchParams({channel: channelId}))).data;
        const channel = record(info?.channel);
        if (info?.ok !== true || channel?.id !== channelId || channel.is_member !== true || channel.is_archived === true
            || channel.is_mpim === true || (channel.is_channel !== true && channel.is_group !== true)) return null;
        const workspace = typeof auth.team === 'string' && auth.team.trim() ? auth.team.trim().slice(0, 80) : auth.team_id;
        const label = typeof channel.name === 'string' && channel.name.trim() ? '#' + channel.name.trim().slice(0, 80) : channelId;
        const targetAccount = auth.team_id + ':' + channelId;
        return [{ref: targetAccount, targetAccount, targetDisplay: `${workspace} / ${label} (${channelId})`}];
    } catch { return null; }
}

// Slack uses these three characters for message markup, including active mentions.
const slackText = (text: string) => text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');

function refusal(answer: Awaited<ReturnType<typeof call>>): ProviderResult | null {
    if ([401, 403, 429].includes(answer.status)) return rejected('slack-refused-' + answer.status);
    const error = answer.data?.error;
    return answer.data?.ok === false && typeof error === 'string' && refused.has(error) ? rejected('slack-' + error) : null;
}

async function sendText(token: string, channelId: string, text: string): Promise<ProviderResult> {
    const answer = await call(token, 'chat.postMessage', {channel: channelId, text: slackText(text), mrkdwn: true,
        parse: 'none', unfurl_links: false, unfurl_media: false});
    const refusalResult = refusal(answer);
    if (refusalResult) return refusalResult;
    if (answer.data?.ok !== true || answer.data.channel !== channelId || typeof answer.data.ts !== 'string'
        || !slackTimestamp.test(answer.data.ts)) throw Error('Unconfirmed Slack message.');
    return {status: 'completed', receipt: channelId + '/' + answer.data.ts};
}

function uploadUrl(value: unknown): URL | null {
    if (typeof value !== 'string') return null;
    try {
        const url = new URL(value);
        return url.origin === 'https://files.slack.com' && url.pathname.startsWith('/upload/v1/')
            && !url.username && !url.password && !url.hash ? url : null;
    } catch { return null; }
}

async function sendImage({env, accountId, post, secret}: PublishCall, channelId: string): Promise<ProviderResult> {
    const file = post.media[0];
    const extension = extensions[file.contentType];
    if (!extension || post.media.length !== 1 || file.bytes > maxUploadBytes) return rejected('media-unavailable');
    const filename = 'image' + extension;
    let fileId: string;
    try {
        const object = await openMedia(env, accountId, file.mediaId);
        if (!object || object.size !== file.bytes) {
            void object?.body.cancel().catch(() => {});
            return rejected('media-unavailable');
        }
        const bytes = await new Response(object.body).arrayBuffer();
        if (bytes.byteLength !== file.bytes) return rejected('media-unavailable');
        const fields = new URLSearchParams({filename, length: String(file.bytes)});
        if (file.alt) fields.set('alt_txt', file.alt);
        const ticket = (await call(secret, 'files.getUploadURLExternal', fields)).data;
        const url = uploadUrl(ticket?.upload_url);
        if (ticket?.ok !== true || typeof ticket.file_id !== 'string' || !fileIdPattern.test(ticket.file_id) || !url)
            return rejected('slack-upload-unavailable');
        fileId = ticket.file_id;
        // The signed file URL takes only the approved bytes. The bot credential stays on slack.com/api.
        const upload = await fetch(url, {method: 'POST', redirect: 'manual', signal: AbortSignal.timeout(120_000),
            headers: {'content-type': 'application/octet-stream'}, body: bytes});
        drop(upload);
        if (upload.status !== 200) return rejected('slack-upload-unavailable');
    } catch {
        // An uncompleted upload is discarded by Slack. No channel share has been attempted.
        return rejected('slack-upload-unavailable');
    }
    // Like Hermes's one-file send, share the image and its caption together. Only this call makes it visible.
    const fields = new URLSearchParams({files: JSON.stringify([{id: fileId, title: filename}]), channel_id: channelId});
    if (post.text) fields.set('initial_comment', slackText(post.text));
    const answer = await call(secret, 'files.completeUploadExternal', fields, 120_000);
    const refusalResult = refusal(answer);
    if (refusalResult) return refusalResult;
    const files = answer.data?.files;
    if (answer.data?.ok !== true || !Array.isArray(files) || files.length !== 1 || record(files[0])?.id !== fileId)
        throw Error('Unconfirmed Slack image share.');
    return {status: 'completed', receipt: channelId + '/' + fileId};
}

export const slack: PublishingAdapter = {
    name: 'slack',
    contractRevision: 'slack-post-v1',
    rules: {
        text: {max: 4000, unit: 'characters'},
        media: {max: 1, types: Object.fromEntries(Object.keys(extensions).map(type => [type, maxUploadBytes])), maxTotalBytes: maxUploadBytes, maxAlt: 1000},
        schedule: null,
        note: 'An image and its caption are shared together. Slack mentions are sent as text.'
    },
    connector: {
        label: 'Slack',
        replaces: false,
        fields: [{name: 'botToken', label: 'Bot token', secret: true, pattern: 'xoxb-[A-Za-z0-9\\-]{1,2043}'},
            {name: 'channelId', label: 'Channel ID', secret: false, placeholder: 'C0123456789', pattern: '[CG][A-Z0-9]{1,24}'}],
        help: 'Install a Slack app with chat:write, files:write and channels:read (groups:read for private channels). Add its bot to the channel and enter the bot token and channel ID.',
        verify: async ({botToken, channelId}) => verifiedChannel(botToken ?? '', channelId ?? '')
    },
    async publish(call) {
        const target = targetPattern.exec(call.targetAccount);
        if (!target || !tokenPattern.test(call.secret)) return rejected('credential-refused');
        return call.post.media.length ? sendImage(call, target[2]) : sendText(call.secret, target[2], call.post.text);
    }
};
