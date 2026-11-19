/**
 * Adapts the Bot Framework token exchange and text activity from Hermes Agent's Teams standalone sender.
 * https://github.com/NousResearch/hermes-agent/blob/4ae451d4126327c871fd084e60c045dec0c85911/plugins/platforms/teams/adapter.py#L191
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
import { base64url } from './jose.ts';
import type { PublishingAdapter } from './publishing.ts';

const guid = '[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}';
const conversation = '[A-Za-z0-9][A-Za-z0-9:@_.\\-]{0,239}';
const service = 'https://smba\\.trafficmanager\\.net/[a-z0-9][a-z0-9.\\-]{0,63}(?:/' + guid + ')?/?';
const guidPattern = new RegExp('^' + guid + '$'), conversationPattern = new RegExp('^' + conversation + '$');
const servicePattern = new RegExp('^' + service + '$');
const secretPattern = /^[\x21-\x7e]{1,2048}$/;
const bearerPattern = /^[A-Za-z0-9._~+/-]{1,8192}={0,2}$/;
const activityIdPattern = /^[A-Za-z0-9][A-Za-z0-9:@._-]{0,191}$/;
const definitelyRefused = new Set([400, 401, 403, 404, 405]);
const timeoutMs = 15_000;
const maxResponseBytes = 64 * 1024;

type Target = {tenantId: string; clientId: string; conversationId: string; serviceUrl: string};

function checkedTarget({tenantId = '', clientId = '', conversationId = '', serviceUrl = ''}: Partial<Target>): Target | null {
    // The commercial connector host is fixed. The path may be global, regional or scoped to a tenant.
    if (!guidPattern.test(tenantId) || !guidPattern.test(clientId) || !conversationPattern.test(conversationId) || !servicePattern.test(serviceUrl))
        return null;
    return {tenantId: tenantId.toLowerCase(), clientId: clientId.toLowerCase(), conversationId, serviceUrl: serviceUrl.replace(/\/?$/, '/')};
}

const targetKey = (target: Target) => [target.tenantId, target.clientId, target.conversationId, target.serviceUrl].join('|');
const conversationUrl = (target: Target) => target.serviceUrl + 'v3/conversations/' + encodeURIComponent(target.conversationId);

function readTarget(key: string): Target | null {
    const [tenantId, clientId, conversationId, serviceUrl] = key.split('|');
    const target = checkedTarget({tenantId, clientId, conversationId, serviceUrl});
    return target && targetKey(target) === key ? target : null;
}

const drop = (response: Response) => { void response.body?.cancel().catch(() => {}); };

async function boundedJson(response: Response): Promise<Record<string, unknown> | null> {
    const reader = response.body?.getReader();
    if (!reader) return null;
    const decoder = new TextDecoder('utf-8', {fatal: true});
    try {
        let text = '', bytes = 0;
        for (;;) {
            const chunk = await reader.read();
            if (chunk.done) break;
            bytes += chunk.value.byteLength;
            if (bytes > maxResponseBytes) return null;
            text += decoder.decode(chunk.value, {stream: true});
        }
        const value: unknown = JSON.parse(text + decoder.decode());
        return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null;
    } catch { return null; }
    finally { void reader.cancel().catch(() => {}); }
}

async function botToken(target: Target, clientSecret: string): Promise<string | null> {
    try {
        const response = await fetch('https://login.microsoftonline.com/' + target.tenantId + '/oauth2/v2.0/token', {
            method: 'POST', redirect: 'manual', signal: AbortSignal.timeout(timeoutMs),
            headers: {'content-type': 'application/x-www-form-urlencoded'},
            body: new URLSearchParams({grant_type: 'client_credentials', client_id: target.clientId, client_secret: clientSecret,
                scope: 'https://api.botframework.com/.default'})
        });
        if (response.status !== 200) { drop(response); return null; }
        const token = (await boundedJson(response))?.access_token;
        return typeof token === 'string' && bearerPattern.test(token) ? token : null;
    } catch { return null; }
}

async function verify(fields: Readonly<Record<string, string>>) {
    const target = checkedTarget(fields);
    const secret = fields.clientSecret ?? '';
    if (!target || !secretPattern.test(secret)) return null;
    const token = await botToken(target, secret);
    if (!token) return null;
    // Teams accepts a minimum page size of 50 for this roster endpoint.
    const response = await fetch(conversationUrl(target) + '/pagedmembers?pageSize=50', {
        redirect: 'manual', signal: AbortSignal.timeout(timeoutMs), headers: {authorization: 'Bearer ' + token}
    });
    if (response.status !== 200) { drop(response); return null; }
    const members = (await boundedJson(response))?.members;
    if (!Array.isArray(members) || !members.length || typeof members[0]?.id !== 'string' || !members[0].id) return null;
    const targetAccount = targetKey(target);
    const ref = base64url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(targetAccount))));
    return [{ref, targetAccount, targetDisplay: `Teams ${target.conversationId} / Bot ${target.clientId} / Tenant ${target.tenantId}`}];
}

export const teams: PublishingAdapter = {
    name: 'teams',
    contractRevision: 'teams-post-v1',
    rules: {
        text: {max: 4000, unit: 'characters'},
        media: {max: 0, types: {}, maxAlt: 0},
        schedule: null,
        note: 'Sends text using Teams Markdown formatting to the connected conversation.'
    },
    connector: {
        label: 'Microsoft Teams',
        replaces: false,
        fields: [
            {name: 'tenantId', label: 'Entra tenant ID', secret: false, pattern: guid},
            {name: 'clientId', label: 'Bot application ID', secret: false, pattern: guid},
            {name: 'clientSecret', label: 'Bot client secret', secret: true},
            {name: 'serviceUrl', label: 'Bot service URL', secret: false, pattern: service, placeholder: 'https://smba.trafficmanager.net/teams/'},
            {name: 'conversationId', label: 'Conversation ID', secret: false, pattern: conversation}
        ],
        help: 'Install your single-tenant Teams bot in the conversation. Enter the service URL and conversation ID from the bot’s activity.',
        verify
    },
    async publish({targetAccount, secret, post}) {
        const target = readTarget(targetAccount);
        if (!target || !secretPattern.test(secret)) return {status: 'rejected', reason: 'credential-refused'};
        const token = await botToken(target, secret);
        if (!token) return {status: 'rejected', reason: 'teams-token-unavailable'};
        const response = await fetch(conversationUrl(target) + '/activities', {
            method: 'POST', redirect: 'manual', signal: AbortSignal.timeout(timeoutMs),
            headers: {authorization: 'Bearer ' + token, 'content-type': 'application/json'},
            body: JSON.stringify({type: 'message', text: post.text, textFormat: 'markdown'})
        });
        if (response.status !== 200 && response.status !== 201) {
            drop(response);
            if (definitelyRefused.has(response.status)) return {status: 'rejected', reason: 'teams-refused-' + response.status};
            throw Error('Unconfirmed Teams activity.');
        }
        const id = (await boundedJson(response))?.id;
        if (typeof id !== 'string' || !activityIdPattern.test(id)) throw Error('Unconfirmed Teams activity.');
        return {status: 'completed', receipt: id};
    }
};
