#!/usr/bin/env node
// Real loopback relay + independent endpoint processes, using synthetic data only.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { createInterface } from 'node:readline';
import { DatabaseSync } from 'node:sqlite';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
assert(args.every((arg, index) => arg === '--json' || arg === '--state-directory' || args[index - 1] === '--state-directory'), 'Unknown demo option');
const host = process.env.WEAVE_DEMO_HOST ?? fileURLToPath(new URL('../../hosts/Weave.Mailbox.Host/bin/Release/net10.0/Weave.Mailbox.Host.dll', import.meta.url));
assert(existsSync(host), 'Build hosts/Weave.Mailbox.Host in Release first');
const worker = fileURLToPath(new URL('./agent.ts', import.meta.url));
const retained = args.includes('--state-directory');
const selected = retained ? args[args.indexOf('--state-directory') + 1] : mkdtempSync(join(tmpdir(), 'weave-agent-network-'));
assert(selected, 'A state directory is required');
const root = resolve(selected);
if (retained) { if (existsSync(root)) assert.equal(readdirSync(root).length, 0, 'Use an empty synthetic state directory'); else mkdirSync(root, { recursive: true, mode: 0o700 }); }
const aliceId = 'alice/group', bobId = 'bob/team%2F界';
const secret = () => randomBytes(32).toString('hex');
const aliceControl = secret(), bobControl = secret(), aliceDirect = secret(), bobDirect = secret(), cryptoKey = secret();
const sensitive = [aliceControl, bobControl, aliceDirect, bobDirect, cryptoKey];
const allowed = new Set(['path', 'dotnet_root', 'ld_library_path', 'lang', 'lc_all', 'tmpdir', 'systemroot', 'windir', 'temp', 'tmp', 'comspec', 'pathext']);
const inherited = Object.fromEntries(Object.entries(process.env).filter(([name]) => allowed.has(name.toLowerCase())));
const abort = new AbortController();
const timeout = setTimeout(() => abort.abort(), 60000);
process.once('SIGTERM', () => abort.abort()); process.once('SIGINT', () => abort.abort());
const children = [], items = [], processes = [], terminatedPids = [];
let relayLog = '';
function whileRunning(promise) {
    return new Promise((resolve, reject) => {
        const cancelled = () => reject(abort.signal.reason);
        abort.signal.addEventListener('abort', cancelled, { once: true });
        const remove = () => abort.signal.removeEventListener('abort', cancelled);
        promise.then(value => { remove(); resolve(value); }, error => { remove(); reject(error); });
        if (abort.signal.aborted) cancelled();
    });
}
function settledWithin(promise, milliseconds) {
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => resolve(false), milliseconds);
        promise.then(() => { clearTimeout(timer); resolve(true); }, error => { clearTimeout(timer); reject(error); });
    });
}
function start(executable, arguments_, environment, kind) {
    const child = spawn(executable, arguments_, { cwd: root, env: { ...inherited, ...environment }, stdio: ['pipe', 'pipe', 'pipe'] });
    children.push(child); const exited = new Promise(resolve => child.once('close', resolve));
    const ready = Promise.withResolvers(); const pending = new Map(); let next = 0, output = 0;
    child.once('error', ready.reject);
    child.once('close', () => { ready.reject(new Error(`${kind} exited before readiness`)); for (const item of pending.values()) item.reject(new Error(`${kind} exited`)); pending.clear(); });
    const drain = async (stream, stdout) => {
        const lines = createInterface({ input: stream, crlfDelay: Infinity });
        for await (const line of lines) {
            output += Buffer.byteLength(line); assert(output <= 262144, `${kind} output exceeded limit`);
            assert(sensitive.every(value => !line.includes(value)), 'Synthetic control material leaked');
            if (kind === 'relay') { relayLog += line + '\n'; const match = line.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/); if (match) ready.resolve({ url: match[1] }); }
            else if (stdout) { const message = JSON.parse(line); if (message.ready) ready.resolve(message);
                else { const item = pending.get(message.id); assert(item, 'Unexpected endpoint response'); pending.delete(message.id);
                    if (message.error) item.reject(new Error(`${kind} ${item.op} command failed`)); else item.resolve(message.result); } }
        }
    };
    const drains = Promise.all([drain(child.stdout, true), drain(child.stderr, false)]);
    // Observe background faults immediately, then await them during cleanup.
    drains.catch(error => { ready.reject(error); abort.abort(); });
    const rpc = async input => {
        abort.signal.throwIfAborted();
        assert(pending.size === 0, 'Only one fixture command may be outstanding');
        const id = ++next; const result = Promise.withResolvers(); pending.set(id, { ...result, op: input.op });
        assert(child.stdin.write(JSON.stringify({ ...input, id }) + '\n'), 'Fixture command buffer is full');
        try { return await whileRunning(result.promise); }
        finally { if (!abort.signal.aborted) pending.delete(id); }
    };
    const item = { child, exited, drains, ready: ready.promise, rpc, kind }; items.push(item); return item;
}
async function stop(item) {
    if (item.child.pid && item.child.exitCode === null && item.child.signalCode === null) {
        item.child.kill('SIGTERM');
        const finished = await settledWithin(item.exited, 5000);
        if (!finished) item.child.kill('SIGKILL');
    }
    assert(await settledWithin(item.exited, 5000), 'Child close could not be confirmed');
    assert(await settledWithin(item.drains, 5000), 'Child pipe drain could not be confirmed');
    if (item.child.pid && !terminatedPids.includes(item.child.pid)) terminatedPids.push(item.child.pid);
}
const checkAbort = () => abort.signal.throwIfAborted();
async function ready(item) {
    const value = await whileRunning(item.ready); processes.push({ kind: item.kind, pid: item.child.pid }); return value;
}
async function startRelay(create, database = join(root, 'relay.db')) {
    const environment = { ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_ENVIRONMENT: 'Production', DOTNET_NOLOGO: 'true',
        Mailbox__DatabasePath: database, Mailbox__AllowCreateStorage: String(create),
        Mailbox__Controls__0__MailboxId: aliceId, Mailbox__Controls__0__Sha256: createHash('sha256').update(aliceControl).digest('hex'),
        Mailbox__Controls__1__MailboxId: bobId, Mailbox__Controls__1__Sha256: createHash('sha256').update(bobControl).digest('hex'),
        Logging__LogLevel__Default: 'Warning' };
    // Colon-delimited category env keys keep the readiness event without request-body logs.
    environment['Logging__LogLevel__Microsoft.Hosting.Lifetime'] = 'Information';
    const item = start('dotnet', [host, '--urls', 'http://127.0.0.1:0'], environment, 'relay');
    const info = await ready(item); return { ...item, url: info.url };
}
async function startAgent(name, mailboxId, control, directControl, relayUrl) {
    const item = start(process.execPath, [worker], { WEAVE_DEMO_MAILBOX: mailboxId, WEAVE_DEMO_RELAY: relayUrl,
        WEAVE_DEMO_CONTROL: control, WEAVE_DEMO_DIRECT_CONTROL: directControl, WEAVE_DEMO_CRYPTO_KEY: cryptoKey,
        WEAVE_DEMO_STATE: join(root, name) }, name);
    return { ...item, ...(await ready(item)) };
}
async function call(agent, method, path, body, expected = 200, anonymous = false) {
    checkAbort(); const response = await agent.rpc({ op: 'relay', method, path, body, anonymous });
    assert.equal(response.status, expected, `${agent.kind} ${method} ${path}`); return response.value;
}
const status = async (agent, method, path, body) => (await agent.rpc({ op: 'relay', method, path, body })).status;
const makePayload = (agent, text, encrypted = false, ttlMs = 60000) => agent.rpc({ op: 'payload', text, encrypted, ttlMs });
const ack = (agent, senderMailboxId, payload) => call(agent, 'POST', `/v1/inbox/${payload.messageId}/ack`, { senderMailboxId });
const summary = (agent, requestId) => call(agent, 'GET', `/v1/contacts/requests/${requestId}?requesterMailboxId=${encodeURIComponent(aliceId)}`);
let alice, bob, relayHost; let report;
const onAbort = () => { for (const child of children) if (child.exitCode === null && child.signalCode === null) child.kill('SIGTERM'); };
abort.signal.addEventListener('abort', onAbort);
try {
    relayHost = await startRelay(true);
    alice = await startAgent('alice', aliceId, aliceControl, aliceDirect, relayHost.url);
    bob = await startAgent('bob', bobId, bobControl, bobDirect, relayHost.url);
    const cardDefinitions = [
        ['bob-public-pending', 'public', null, 'pending', 'pending'],
        ['bob-public-reject', 'public', 60000, 'reject', 'rejected'],
        ['bob-public-auto', 'public', null, 'autoaccept', 'accepted'],
        ['bob-private-manual', 'unlisted', null, 'manual', 'needsAction'],
        ['bob-private-auto', 'unlisted', 120000, 'autoaccept', 'accepted'],
    ];
    for (const [cardId, visibility, ttl, policy] of cardDefinitions) {
        const now = Date.now(); await bob.rpc({ op: 'publish', policy, card: { cardId, visibility, createdAt: new Date(now).toISOString(),
            expiresAt: ttl === null ? null : new Date(now + ttl).toISOString(), revokedAt: null, audienceHint: visibility === 'unlisted' ? 'intranet' : null,
            methods: [{ methodId: 'relay', version: 1, transport: 'relay', endpoint: relayHost.url, instructions: 'Opaque contact attempt; recipient independently decides' },
                { methodId: 'direct', version: 1, transport: 'direct', endpoint: bob.endpoint, instructions: 'Synthetic direct fixture; separate recipient admission and control exchange required' }] } });
    }
    const cards = { public: (await call(alice, 'GET', '/v1/contacts/cards', undefined, 200, true)).items,
        owned: (await call(bob, 'GET', '/v1/contacts/owned-cards')).items,
        privateDiscoveryStatus: await status(alice, 'GET', '/v1/contacts/card?cardId=bob-private-manual'),
        privateExport: await call(bob, 'GET', '/v1/contacts/card?cardId=bob-private-manual') };
    const policies = []; let connected; let reply;
    for (const [cardId, visibility, ttl, policy, expectedStatus] of cardDefinitions) {
        const body = await makePayload(alice, 'synthetic contact attempt');
        const initial = await call(alice, 'POST', '/v1/contacts/requests', { requestId: body.messageId, cardId, methodId: 'relay', payload: body });
        const optional = policy === 'manual' ? await makePayload(bob, 'synthetic optional reply') : null;
        const decision = await bob.rpc({ op: 'policy', requestId: body.messageId, requesterMailboxId: aliceId, reply: optional });
        const requesterView = await summary(alice, body.messageId), recipientView = await summary(bob, body.messageId);
        policies.push({ cardId, visibility, policy, expectedStatus, initial, requesterView, recipientView });
        await ack(bob, aliceId, body);
        if (optional) { reply = (await alice.rpc({ op: 'consume' })).items[0]; await ack(alice, bobId, optional); }
        if (expectedStatus === 'accepted') connected = decision;
        if (cardId !== 'bob-private-auto') {
            // End each independent policy scenario explicitly; one mailbox pair has one current contact.
            const fenced = await call(bob, 'PUT', '/v1/contacts/blocks', { peerMailboxId: aliceId, expectedGeneration: initial.generation });
            await call(bob, 'DELETE', '/v1/contacts/blocks', { peerMailboxId: aliceId, expectedGeneration: fenced.generation });
        }
    }
    const plain = await makePayload(alice, 'synthetic plaintext body');
    const encrypted = await makePayload(alice, 'synthetic encrypted body', true);
    const envelope = payload => ({ version: 1, recipientMailboxId: bobId, contactGeneration: connected.generation, payload });
    const firstReceipt = await call(alice, 'POST', '/v1/messages', envelope(plain));
    const retryReceipt = await call(alice, 'POST', '/v1/messages', envelope(plain));
    await call(alice, 'POST', '/v1/messages', envelope(encrypted));
    const credentialScopes = { aliceForeignAckStatus: await status(alice, 'POST', `/v1/inbox/${plain.messageId}/ack`, { senderMailboxId: aliceId }),
        bobForeignAckStatus: await status(bob, 'POST', `/v1/inbox/${reply.envelope.payload.messageId}/ack`, { senderMailboxId: bobId }) };
    const firstPull = await bob.rpc({ op: 'consume' }); const repeatedPull = await bob.rpc({ op: 'consume' });
    const afterPullReceipt = await call(alice, 'GET', `/v1/outbox/${plain.messageId}`);
    const sse = await bob.rpc({ op: 'sse', count: 2, lastEventId: encrypted.messageId });
    const afterSseReceipt = await call(alice, 'GET', `/v1/outbox/${plain.messageId}`);
    const database = new DatabaseSync(join(root, 'relay.db'), { readOnly: true });
    let activePayloads;
    try { activePayloads = [plain, encrypted].map(body => { const row = database.prepare('SELECT id,sender,recipient,payload,state FROM mailbox_messages WHERE sender=? AND id=?').get(aliceId, body.messageId);
        assert(row); return { messageId: row.id, senderMailboxId: row.sender, recipientMailboxId: row.recipient, state: row.state, bytes: Buffer.from(row.payload).toString('base64') }; }); }
    finally { database.close(); }
    await stop(bob); bob = await startAgent('bob', bobId, bobControl, bobDirect, relayHost.url);
    const afterRestartPull = await bob.rpc({ op: 'consume' }); const localAfterRestart = await bob.rpc({ op: 'state' });
    const acknowledged = await ack(bob, aliceId, plain); await ack(bob, aliceId, encrypted);
    const repeatedAck = await ack(bob, aliceId, plain); const afterAckPull = await bob.rpc({ op: 'consume' });
    const delivery = { plain, encrypted, firstReceipt, retryReceipt, firstPull, repeatedPull, afterPullReceipt, sse, afterSseReceipt,
        afterRestartPull, localAfterRestart, activePayloads, ack: acknowledged, repeatedAck, afterAckPull };
    const expiring = await makePayload(alice, 'synthetic expiring body', false, 1500);
    await call(alice, 'POST', '/v1/messages', envelope(expiring));
    await delay(Math.max(0, Date.parse(expiring.expiresAt) - Date.now() + 100), undefined, { signal: abort.signal });
    const expiredReceipt = await call(alice, 'GET', `/v1/outbox/${expiring.messageId}`); const expiredPull = await bob.rpc({ op: 'consume' });
    const blocked = await makePayload(alice, 'synthetic blocked body'); await call(alice, 'POST', '/v1/messages', envelope(blocked));
    const channel = await call(bob, 'PUT', '/v1/contacts/blocks', { peerMailboxId: aliceId, expectedGeneration: connected.generation });
    const blockedReceipt = await call(alice, 'GET', `/v1/outbox/${blocked.messageId}`);
    const blockedSendStatus = await status(alice, 'POST', '/v1/messages', envelope(await makePayload(alice, 'synthetic blocked send')));
    const contactWhileBlocked = await makePayload(alice, 'synthetic blocked contact');
    const blockedContactStatus = await status(alice, 'POST', '/v1/contacts/requests', { requestId: contactWhileBlocked.messageId,
        cardId: 'bob-public-auto', methodId: 'relay', payload: contactWhileBlocked });
    await call(bob, 'DELETE', '/v1/contacts/blocks', { peerMailboxId: aliceId, expectedGeneration: channel.generation });
    const afterUnblockPull = await bob.rpc({ op: 'consume' });
    const staleSendStatus = await status(alice, 'POST', '/v1/messages', envelope(await makePayload(alice, 'synthetic stale generation')));
    const freshBody = await makePayload(alice, 'synthetic fresh contact');
    await call(alice, 'POST', '/v1/contacts/requests', { requestId: freshBody.messageId, cardId: 'bob-public-auto', methodId: 'relay', payload: freshBody });
    const freshContact = await bob.rpc({ op: 'policy', requestId: freshBody.messageId, requesterMailboxId: aliceId }); await ack(bob, aliceId, freshBody);
    const freshPayload = await makePayload(alice, 'synthetic renewed body');
    const freshReceipt = await call(alice, 'POST', '/v1/messages', { ...envelope(freshPayload), contactGeneration: freshContact.generation }); await ack(bob, aliceId, freshPayload);
    await alice.rpc({ op: 'authorizeDirect', peerMailboxId: bobId }); await bob.rpc({ op: 'authorizeDirect', peerMailboxId: aliceId });
    const directPayload = await makePayload(alice, 'synthetic direct body');
    const directEndpoint = cards.privateExport.methods.find(method => method.methodId === 'direct').endpoint;
    assert.equal(directEndpoint, bob.endpoint, 'Endpoint restart must preserve the advertised direct listener');
    const directInput = { op: 'direct', endpoint: directEndpoint, control: bobDirect, payload: directPayload };
    const directFirst = await alice.rpc(directInput); assert.equal(directFirst.status, 200);
    const directRetry = await alice.rpc(directInput); assert.equal(directRetry.status, 200);
    const direct = { payload: directPayload, first: directFirst.value, retry: directRetry.value, local: await bob.rpc({ op: 'state' }),
        outboxStatus: await status(alice, 'GET', `/v1/outbox/${directPayload.messageId}`) };
    const directDatabase = new DatabaseSync(join(root, 'relay.db'), { readOnly: true });
    try { direct.relayRowCount = directDatabase.prepare('SELECT count(*) AS count FROM mailbox_messages WHERE id=?').get(directPayload.messageId).count; }
    finally { directDatabase.close(); }
    await stop(relayHost); relayHost = await startRelay(false);
    // An endpoint restart retains its receipts/policies; relay restart retains terminal bodies and metadata.
    const response = await fetch(relayHost.url + `/v1/outbox/${plain.messageId}`, { headers: { 'X-Weave-Mailbox-Control': aliceControl }, signal: abort.signal });
    assert.equal(response.status, 200); const afterRelayRestartReceipt = await response.json();
    report = { processes, credentialScopes, cards, policies, reply, delivery,
        lifecycle: { expiredReceipt, expiredPull, blockedReceipt, blockedSendStatus, blockedContactStatus, afterUnblockPull, staleSendStatus,
            freshContact, freshReceipt, afterRelayRestartReceipt }, direct };
} finally {
    clearTimeout(timeout);
    const stopped = await Promise.allSettled(items.map(stop));
    assert(stopped.every(item => item.status === 'fulfilled'), 'Child process cleanup failed');
    abort.signal.removeEventListener('abort', onAbort);
    for (const name of ['alice', 'bob']) {
        const file = join(root, name, 'state.json');
        if (existsSync(file)) assert(sensitive.every(value => !readFileSync(file, 'utf8').includes(value)), 'Synthetic control material persisted in endpoint state');
    }
    if (!retained) rmSync(root, { recursive: true, force: true });
}
assert(report, 'Demo did not complete');
report.terminatedPids = terminatedPids; report.relayLog = relayLog;
if (args.includes('--json')) console.log(JSON.stringify(report));
else console.log('Real independent processes: public pending/reject/explicit autoaccept and private policy; opaque plaintext/encrypted relay delivery; pull/SSE retry and endpoint dedup; explicit ACK/expiry/block; retained restart; direct delivery with no relay outbox receipt.\nSynthetic fixture only: no hosted-agent wake, business execution, production E2EE adapter or NAT traversal.\n' + (retained ? `Synthetic state retained in ${root}` : 'Temporary synthetic state removed; all child processes stopped.'));
