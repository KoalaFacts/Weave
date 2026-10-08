#!/usr/bin/env node
// Synthetic endpoint fixture. No model, hosted-agent wake or production E2EE adapter.
import assert from 'node:assert/strict';
import { createCipheriv, createDecipheriv, createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { join } from 'node:path';
import { createInterface } from 'node:readline';

const required = name => { assert(process.env[name], `Missing ${name}`); return process.env[name]; };
const mailboxId = required('WEAVE_DEMO_MAILBOX');
const relay = required('WEAVE_DEMO_RELAY');
const control = required('WEAVE_DEMO_CONTROL');
const directControl = required('WEAVE_DEMO_DIRECT_CONTROL');
const key = Buffer.from(required('WEAVE_DEMO_CRYPTO_KEY'), 'hex');
const directory = required('WEAVE_DEMO_STATE');
mkdirSync(directory, { recursive: true, mode: 0o700 });
const file = join(directory, 'state.json');
const state = existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : { mailboxId, cards: {}, peers: [], receipts: [] };
assert.equal(state.mailboxId, mailboxId);
const save = () => { writeFileSync(file + '.tmp', JSON.stringify(state), { mode: 0o600 }); renameSync(file + '.tmp', file); };
save();
const lifetime = new AbortController();
const signal = () => AbortSignal.any([lifetime.signal, AbortSignal.timeout(15000)]);
const limit = 262144;
async function bytes(stream, maximum = limit) {
    const chunks = []; let length = 0;
    for await (const chunk of stream) { length += chunk.length; assert(length <= maximum, 'Fixture response bound exceeded'); chunks.push(chunk); }
    return Buffer.concat(chunks);
}
async function request(url, method = 'GET', body, headers = {}) {
    const response = await fetch(url, { method, headers: { ...headers, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
        body: body === undefined ? undefined : JSON.stringify(body), redirect: 'error', signal: signal() });
    return { status: response.status, value: JSON.parse((await bytes(response.body)).toString()) };
}
async function call(method, path, body, anonymous = false) {
    assert(path.startsWith('/v1/'), 'Only mailbox fixture routes are supported');
    const result = await request(relay + path, method, body, anonymous ? {} : { 'X-Weave-Mailbox-Control': control });
    // This small example refuses unsafe generations instead of silently rounding int64.
    const check = value => { if (!value || typeof value !== 'object') return;
        for (const [name, item] of Object.entries(value)) { if (name === 'generation') assert(Number.isSafeInteger(item), 'Generation exceeds this demo client precision'); check(item); } };
    check(result.value); return result;
}
function uuid7(now) {
    const value = randomBytes(16); value.writeUIntBE(now, 0, 6); value[6] = 0x70 | (value[6] & 0x0f); value[8] = 0x80 | (value[8] & 0x3f);
    const hex = value.toString('hex'); return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
function payload(text, encrypted = false, ttlMs = 60000) {
    const now = Date.now(); let body = Buffer.from(text); let encoding = 'text/plain';
    if (encrypted) { const nonce = randomBytes(12); const cipher = createCipheriv('aes-256-gcm', key, nonce);
        body = Buffer.concat([nonce, cipher.update(body), cipher.final(), cipher.getAuthTag()]); encoding = 'demo/aes-256-gcm'; }
    return { messageId: uuid7(now), createdAt: new Date(now).toISOString(), expiresAt: new Date(now + ttlMs).toISOString(),
        payloadEncoding: encoding, bytes: body.toString('base64') };
}
function receive(senderMailboxId, body, transport) {
    assert(state.receipts.length < 64, 'Fixture receipt capacity reached');
    const digest = createHash('sha256').update(JSON.stringify(body)).digest('hex');
    const existing = state.receipts.find(r => r.senderMailboxId === senderMailboxId && r.messageId === body.messageId);
    if (existing) { assert.equal(existing.digest, digest, 'Conflicting local delivery'); existing.deliveries++; save(); return { accepted: true, duplicate: true }; }
    let content = Buffer.from(body.bytes, 'base64'); assert(content.length <= 65536);
    if (body.payloadEncoding === 'demo/aes-256-gcm') { const decipher = createDecipheriv('aes-256-gcm', key, content.subarray(0, 12));
        decipher.setAuthTag(content.subarray(-16)); content = Buffer.concat([decipher.update(content.subarray(12, -16)), decipher.final()]); }
    state.receipts.push({ senderMailboxId, messageId: body.messageId, digest, text: content.toString(), transport, deliveries: 1 }); save();
    return { accepted: true, duplicate: false };
}
async function consumeSse(count, lastEventId) {
    const controller = new AbortController();
    const response = await fetch(relay + '/v1/inbox/events', { headers: { 'X-Weave-Mailbox-Control': control,
        ...(lastEventId ? { 'Last-Event-ID': lastEventId } : {}) }, signal: AbortSignal.any([signal(), controller.signal]) });
    assert.equal(response.status, 200); const events = []; let buffer = ''; let length = 0; const decoder = new TextDecoder();
    try { for await (const chunk of response.body) { length += chunk.length; assert(length <= limit); buffer += decoder.decode(chunk, { stream: true });
        let boundary; while ((boundary = buffer.indexOf('\n\n')) >= 0) { const frame = buffer.slice(0, boundary); buffer = buffer.slice(boundary + 2);
            const data = frame.split('\n').find(line => line.startsWith('data: ')); if (!data) continue;
            const message = JSON.parse(data.slice(6)); receive(message.senderMailboxId, message.envelope.payload, 'relay'); events.push(message);
            if (events.length === count) return events; } }
        throw new Error('SSE ended before requested events');
    } finally { controller.abort(); }
}
const server = createServer(async (req, res) => {
    try {
        assert.equal(req.method, 'POST'); assert.equal(req.url, '/direct');
        const supplied = Buffer.from(String(req.headers['x-weave-demo-direct'] ?? ''));
        const expected = Buffer.from(directControl); assert(supplied.length === expected.length && timingSafeEqual(supplied, expected));
        const input = JSON.parse((await bytes(req, 131072)).toString()); assert(state.peers.includes(input.senderMailboxId), 'Peer not admitted locally');
        assert(Date.parse(input.payload.expiresAt) > Date.now(), 'Direct fixture payload expired');
        res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(receive(input.senderMailboxId, input.payload, 'direct')));
    } catch { res.writeHead(400, { 'Content-Type': 'application/json' }); res.end('{"error":"invalid direct fixture request"}'); }
});
server.requestTimeout = 15000; server.headersTimeout = 15000;
await new Promise(resolve => server.listen(state.directPort ?? 0, '127.0.0.1', resolve));
state.directPort = server.address().port; save();
const endpoint = `http://127.0.0.1:${state.directPort}/direct`;
console.log(JSON.stringify({ ready: true, pid: process.pid, mailboxId, endpoint }));
async function command(input) {
    switch (input.op) {
        case 'relay': return call(input.method, input.path, input.body, input.anonymous);
        case 'payload': return payload(input.text, input.encrypted, input.ttlMs);
        case 'publish': {
            assert(['pending', 'reject', 'autoaccept', 'manual'].includes(input.policy));
            const result = await call('PUT', '/v1/contacts/cards', input.card); assert.equal(result.status, 200, `Card publication returned ${result.status} ${result.value.code}`);
            state.cards[input.card.cardId] = input.policy; save(); return result.value;
        }
        case 'policy': {
            const path = `/v1/contacts/requests/${input.requestId}`;
            const found = await call('GET', path + '?requesterMailboxId=' + encodeURIComponent(input.requesterMailboxId)); assert.equal(found.status, 200);
            const policy = state.cards[found.value.cardId]; assert(policy, 'No recipient policy for card');
            if (policy === 'pending') return { request: found.value, generation: found.value.generation };
            const result = await call('POST', path + '/decision', { requesterMailboxId: input.requesterMailboxId,
                expectedGeneration: found.value.generation, status: policy === 'reject' ? 'rejected' : policy === 'manual' ? 'needsAction' : 'accepted', reply: input.reply ?? null });
            assert.equal(result.status, 200); return result.value;
        }
        case 'consume': {
            const result = await call('GET', '/v1/inbox'); assert.equal(result.status, 200);
            for (const message of result.value.items) receive(message.senderMailboxId, message.envelope.payload, 'relay'); return result.value;
        }
        case 'sse': return consumeSse(input.count, input.lastEventId);
        case 'state': return state;
        case 'authorizeDirect': {
            const channel = await call('GET', '/v1/contacts/channel?peerMailboxId=' + encodeURIComponent(input.peerMailboxId));
            assert.equal(channel.status, 200); assert(!channel.value.isBlockedByMailbox && !channel.value.isBlockedByPeer);
            const locator = channel.value.currentRequest; assert(locator, 'No relay contact for direct fixture admission');
            const relation = await call('GET', `/v1/contacts/requests/${locator.requestId}?requesterMailboxId=${encodeURIComponent(locator.requesterMailboxId)}`);
            assert.equal(relation.status, 200); assert.equal(relation.value.status, 'accepted');
            if (!state.peers.includes(input.peerMailboxId)) state.peers.push(input.peerMailboxId); save(); return { admitted: true };
        }
        case 'direct': {
            const url = new URL(input.endpoint); assert.equal(url.hostname, '127.0.0.1'); assert.equal(url.pathname, '/direct');
            return request(url, 'POST', { senderMailboxId: mailboxId, payload: input.payload }, { 'X-Weave-Demo-Direct': input.control });
        }
        default: throw new Error('Unknown fixture command');
    }
}
const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
const shutdown = () => { lifetime.abort(); lines.close(); server.closeAllConnections(); server.close(); };
process.once('SIGTERM', shutdown); process.once('SIGINT', shutdown);
try {
    for await (const line of lines) {
        assert(Buffer.byteLength(line) <= 131072, 'Fixture command bound exceeded');
        const input = JSON.parse(line);
        try { console.log(JSON.stringify({ id: input.id, result: await command(input) })); }
        catch { console.log(JSON.stringify({ id: input.id, error: 'Endpoint fixture command failed' })); }
    }
} finally { shutdown(); }
