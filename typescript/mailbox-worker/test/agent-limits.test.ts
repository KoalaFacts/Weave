import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { payload, start, type AgentCredential } from './harness.ts';
import { agentKey, clientAssertion, dpopProof, keyEndorsement, keyProof, type TestKey } from './agent-key.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import { issueRecoveryCode } from '../src/recovery-codes.ts';
import { ownerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Participant = AgentCredential & {cardId: string};
type Connection = {a: Participant; b: Participant; generation: number; token: string};

const origin = 'http://localhost';
const tokenUrl = origin + '/v1/token';
const keysUrl = origin + '/v1/keys';
const recoveryUrl = origin + '/v1/key-requests';
const assertionType = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer';
const ticksPerHour = 36000000000n;
const dayTicks = 864000000000n;
// Enough registrations for the multi-agent scenarios; the registration counters are not under test here.
const bindings = {REGISTRATION_PER_IP_PER_HOUR: '1000', REGISTRATION_GLOBAL_PER_HOUR: '10000', REGISTRATION_MAX_ACCOUNTS: '1000'};

const env = (h: Harness) => ({db: h.db, ...bindings, REGISTRATION_ENABLED: 'true'}) as unknown as Env;
const nowTicks = () => BigInt(Date.now()) * 10000n;

async function participant(h: Harness): Promise<Participant> {
    const p = await h.participant();
    return {accountId: p.accountId, key: p.key, cardId: p.cardId};
}
function contactRequest(a: Participant, b: Participant) {
    const intro = payload('introduction');
    return {requestVersion: 2, requestId: intro.messageId, requesterAccountId: a.accountId, requesterAddressId: a.cardId, recipientAccountId: b.accountId, recipientAddressId: b.cardId, payload: intro};
}
async function connect(h: Harness): Promise<Connection> {
    const a = await participant(h), b = await participant(h);
    const state = await h.ok('/v1/contacts/requests', a, 'POST', contactRequest(a, b));
    await h.ok('/v1/contacts/requests/' + state.request.requestId + '/decision', b, 'POST', {requesterAccountId: a.accountId, expectedGeneration: state.generation, status: 'accepted', reply: null});
    const own = await h.ok('/v1/contacts', a);
    return {a, b, generation: state.generation, token: own.items.find((x: {accountId: string}) => x.accountId === b.accountId).connectionToken};
}
const envelope = (c: Connection) => ({version: 2, recipientAccountId: c.b.accountId, contactGeneration: c.generation, connectionToken: c.token, payload: payload('message')});
const sendMessage = (h: Harness, c: Connection, value: unknown) => h.request('/v1/messages', c.a, 'POST', value) as unknown as Promise<Response>;

async function problem(response: Response, status: number, code: string): Promise<Response> {
    const text = await response.clone().text();
    assert.equal(response.status, status, text);
    assert.equal((JSON.parse(text) as {code: string}).code, code, text);
    return response;
}
function assertRetryAfter(response: Response): void {
    const value = Number(response.headers.get('Retry-After'));
    assert.ok(Number.isInteger(value) && value >= 1 && value <= 3600, 'Retry-After ' + response.headers.get('Retry-After'));
}

function tokenRequest(h: Harness, key: TestKey, accountId: string, audience = tokenUrl): Promise<Response> {
    return h.mf.dispatchFetch(tokenUrl, {method: 'POST', headers: {'Content-Type': 'application/x-www-form-urlencoded', DPoP: dpopProof(key, {method: 'POST', url: tokenUrl})},
        body: new URLSearchParams([['grant_type', 'client_credentials'], ['client_assertion_type', assertionType],
            ['client_assertion', clientAssertion(key, {accountId, audience})]]).toString()}) as unknown as Promise<Response>;
}
const accessTokens = async (h: Harness, accountId: string) => Number(await h.db.prepare('SELECT count(*) n FROM agent_access_tokens WHERE account_id=?').bind(accountId).first('n'));
const keyRows = async (h: Harness, accountId: string) => Number(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE account_id=?').bind(accountId).first('n'));

function addKey(h: Harness, cred: AgentCredential, newKey: TestKey): Promise<Response> {
    return h.request('/v1/keys', cred, 'POST', {proof: keyProof(newKey, {accountId: cred.accountId, audience: keysUrl}),
        endorsement: keyEndorsement(cred.key, {accountId: cred.accountId, audience: keysUrl, newKid: newKey.kid})}) as unknown as Promise<Response>;
}
/** Revoked key rows created and revoked at `createdAt`, in batches. */
async function revokedRows(h: Harness, accountId: string, count: number, createdAt: bigint): Promise<void> {
    const stamp = createdAt.toString();
    for (let done = 0; done < count; done += 100) {
        const statements = [];
        for (let n = done; n < Math.min(count, done + 100); n++) {
            // Revoked rows are never parsed, so a random thumbprint-shaped kid avoids generating hundreds of key pairs.
            const kid = randomBytes(32).toString('base64url');
            statements.push(h.db.prepare('INSERT INTO agent_keys(kid,account_id,public_jwk,created_at,revoked_at,updated_at) VALUES(?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER))')
                .bind(kid, accountId, JSON.stringify({crv: 'Ed25519', kty: 'OKP', x: kid}), stamp, stamp, stamp));
        }
        await h.db.batch(statements);
    }
}

test('send_UnclaimedThirtyFirstInHour_429WithRetryAfter', async () => {
    const h = await start({bindings});
    try {
        const c = await connect(h);
        for (let n = 0; n < 30; n++) assert.equal((await sendMessage(h, c, envelope(c))).status, 200);
        const before = await h.db.prepare('SELECT count(*) n FROM mailbox_records').first('n');
        assertRetryAfter(await problem(await sendMessage(h, c, envelope(c)), 429, 'capacity'));
        assert.equal(await h.db.prepare('SELECT count(*) n FROM mailbox_records').first('n'), before);
    } finally { await h.close(); }
});

test('send_RetrySameMessageIdAtLimit_ReturnsOriginalReceipt', async () => {
    const h = await start({bindings});
    try {
        const c = await connect(h), first = envelope(c);
        const original = await (await sendMessage(h, c, first)).json();
        for (let n = 1; n < 30; n++) assert.equal((await sendMessage(h, c, envelope(c))).status, 200);
        const retry = await sendMessage(h, c, first);
        assert.equal(retry.status, 200, await retry.clone().text());
        assert.deepEqual(await retry.json(), original);
        await problem(await sendMessage(h, c, envelope(c)), 429, 'capacity');
    } finally { await h.close(); }
});

test('send_ClaimedAgent_AllowsMoreThanUnclaimed', async () => {
    const h = await start({bindings});
    try {
        const c = await connect(h);
        await h.claim(c.a);
        for (let n = 0; n < 31; n++) assert.equal((await sendMessage(h, c, envelope(c))).status, 200, 'send ' + (n + 1));
    } finally { await h.close(); }
});

test('contactRequest_UnclaimedEleventh_429', async () => {
    const h = await start({bindings});
    try {
        const a = await participant(h), inputs = [];
        for (let n = 0; n < 11; n++) inputs.push(contactRequest(a, await participant(h)));
        for (const input of inputs.slice(0, 10)) assert.equal((await h.request('/v1/contacts/requests', a, 'POST', input)).status, 200);
        assertRetryAfter(await problem(await h.request('/v1/contacts/requests', a, 'POST', inputs[10]) as unknown as Response, 429, 'capacity'));
        assert.equal(await h.db.prepare('SELECT count(*) n FROM contact_requests WHERE requester_account_id=?').bind(a.accountId).first('n'), 10);
        // A retry of an existing request ID returns its original result without consuming the limit.
        assert.equal((await h.request('/v1/contacts/requests', a, 'POST', inputs[0])).status, 200);
    } finally { await h.close(); }
});

test('address_UnclaimedFourth_429', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        const created = [];
        for (let n = 0; n < 3; n++) created.push(await h.address(a));
        await problem(await h.request('/v1/addresses', a, 'POST', {}) as unknown as Response, 429, 'capacity');
        // A revoked address no longer counts as live.
        await h.ok('/v1/addresses/' + created[0].addressId, a, 'DELETE');
        await h.address(a);
    } finally { await h.close(); }
});

test('address_UnclaimedSecondPublic_429AndPrivateStill200', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        await h.address(a, {isPublic: true});
        await problem(await h.request('/v1/addresses', a, 'POST', {isPublic: true}) as unknown as Response, 429, 'capacity');
        await h.address(a, {isPublic: false});
    } finally { await h.close(); }
});

test('address_MakePublicOverLimit_429Unchanged', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        await h.address(a, {isPublic: true});
        const hidden = await h.address(a, {label: 'hidden'});
        await problem(await h.request('/v1/addresses/' + hidden.addressId, a, 'PUT', {label: 'hidden', isPublic: true}) as unknown as Response, 429, 'capacity');
        assert.equal(await h.db.prepare('SELECT is_public FROM account_addresses WHERE address_id=?').bind(hidden.addressId).first('is_public'), 0);
        // Editing it while it stays private is still allowed.
        await h.ok('/v1/addresses/' + hidden.addressId, a, 'PUT', {label: 'renamed'});
    } finally { await h.close(); }
});

test('address_ClaimedFifthPublic_200SixthPublic429', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        await h.claim(a);
        for (let n = 0; n < 5; n++) await h.address(a, {isPublic: true});
        await problem(await h.request('/v1/addresses', a, 'POST', {isPublic: true}) as unknown as Response, 429, 'capacity');
        await h.address(a);
    } finally { await h.close(); }
});

test('token_UnclaimedThirtyFirstInHour_429TemporarilyUnavailable', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        for (let n = 0; n < 30; n++) assert.equal((await tokenRequest(h, a.key, a.accountId)).status, 200, 'token ' + (n + 1));
        const refused = await tokenRequest(h, a.key, a.accountId);
        assert.equal(refused.status, 429);
        assert.deepEqual(await refused.json(), {error: 'temporarily_unavailable'});
        assert.equal(refused.headers.get('Cache-Control'), 'no-store');
        assertRetryAfter(refused);
        assert.equal(await accessTokens(h, a.accountId), 30);
    } finally { await h.close(); }
});

test('token_InvalidAssertionsDoNotConsume', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        for (let n = 0; n < 10; n++) assert.equal((await tokenRequest(h, a.key, a.accountId, origin + '/v1/keys')).status, 401);
        for (let n = 0; n < 30; n++) assert.equal((await tokenRequest(h, a.key, a.accountId)).status, 200, 'token ' + (n + 1));
    } finally { await h.close(); }
});

test('hourlyWindow_PreviousHourCounter_Ignored', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        const hour = nowTicks() / ticksPerHour;
        await h.db.batch([h.db.prepare("UPDATE counters SET value=value+1 WHERE name='record-seq'"),
            h.db.prepare("INSERT INTO rate_limits(seq,policy,bucket_key,window_start,count) VALUES((SELECT value FROM counters WHERE name='record-seq'),'agent-token',?,CAST(? AS INTEGER),30)")
                .bind(a.accountId, ((hour - 1n) * ticksPerHour).toString())]);
        assert.equal((await tokenRequest(h, a.key, a.accountId)).status, 200);
        const row = await h.db.prepare("SELECT CAST(window_start AS TEXT) window_start,count FROM rate_limits WHERE policy='agent-token' AND bucket_key=?").bind(a.accountId)
            .first<{window_start: string; count: number}>();
        assert.deepEqual(row, {window_start: (hour * ticksPerHour).toString(), count: 1});
    } finally { await h.close(); }
});

test('addKey_EleventhKeyRowInDay_429', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        // The registration key plus nine added keys are ten key rows created within 24 h.
        for (let n = 0; n < 9; n++) assert.equal((await addKey(h, a, agentKey())).status, 200, 'add ' + (n + 1));
        const refused = agentKey();
        await problem(await addKey(h, a, refused), 429, 'capacity');
        assert.equal(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE kid=?').bind(refused.kid).first('n'), 0);
        assert.equal(await keyRows(h, a.accountId), 10);
        // Rows older than 24 h no longer count.
        await h.db.prepare('UPDATE agent_keys SET created_at=created_at-CAST(? AS INTEGER),cooling_until=NULL WHERE account_id=?').bind(dayTicks.toString(), a.accountId).run();
        assert.equal((await addKey(h, a, refused)).status, 200);
    } finally { await h.close(); }
});

test('addKey_ConfirmedKeyRequestCountsOnceAgainstDailyAdditions', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        const humanId = await h.claim(a);
        // The registration key plus seven rows are eight additions in the last 24 h.
        await revokedRows(h, a.accountId, 7, nowTicks());
        const confirmed = await addKey(h, a, agentKey());
        assert.equal(confirmed.status, 202, await confirmed.clone().text());
        const {requestId} = await confirmed.json() as {requestId: string};
        assert.deepEqual(await ownerAction(env(h), {humanId, authAt: Date.now(), kind: 'confirm-key', requestId}), {ok: true});
        // Nine additions: had the confirmed request counted as well as its key row, this tenth would be refused.
        assert.equal((await addKey(h, a, agentKey())).status, 202);
        await problem(await addKey(h, a, agentKey()), 429, 'capacity');
    } finally { await h.close(); }
});

test('contactRequest_ClaimedSixtyFirst_429WithRetryAfter', async () => {
    const h = await start({bindings});
    try {
        const a = await participant(h);
        await h.claim(a);
        const inputs = [];
        for (let n = 0; n < 61; n++) inputs.push(contactRequest(a, await participant(h)));
        for (const [n, input] of inputs.slice(0, 60).entries()) assert.equal((await h.request('/v1/contacts/requests', a, 'POST', input)).status, 200, 'request ' + (n + 1));
        assertRetryAfter(await problem(await h.request('/v1/contacts/requests', a, 'POST', inputs[60]) as unknown as Response, 429, 'capacity'));
        assert.equal(await h.db.prepare('SELECT count(*) n FROM contact_requests WHERE requester_account_id=?').bind(a.accountId).first('n'), 60);
    } finally { await h.close(); }
});

test('token_ClaimedHundredTwentyFirst_429WithRetryAfter', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        await h.claim(a);
        for (let n = 0; n < 120; n++) assert.equal((await tokenRequest(h, a.key, a.accountId)).status, 200, 'token ' + (n + 1));
        const refused = await tokenRequest(h, a.key, a.accountId);
        assert.equal(refused.status, 429);
        assert.deepEqual(await refused.json(), {error: 'temporarily_unavailable'});
        assertRetryAfter(refused);
        assert.equal(await accessTokens(h, a.accountId), 120);
    } finally { await h.close(); }
});

test('addKey_ThousandKeyRows_429', async () => {
    const h = await start({bindings});
    try {
        const a = await h.register();
        await revokedRows(h, a.accountId, 999, nowTicks() - dayTicks - ticksPerHour);
        assert.equal(await keyRows(h, a.accountId), 1000);
        await problem(await addKey(h, a, agentKey()), 429, 'capacity');
        assert.equal(await keyRows(h, a.accountId), 1000);
    } finally { await h.close(); }
});

test('recovery_OthersKeyRequestsDoNotBlockButKeyRowsDo', async () => {
    const h = await start({bindings});
    try {
        const human = await signInHuman(env(h), {provider: 'github', subject: '123', providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'owner'});
        const humanId = (human as {humanId: string}).humanId;
        const created = await createOwnedAgent(env(h), {humanId, displayName: 'owned', sourceIp: '203.0.113.7'});
        const agentId = (created as {accountId: string}).accountId;
        const seed = agentKey(), stamp = nowTicks().toString();
        await h.db.prepare('INSERT INTO agent_keys(kid,account_id,public_jwk,created_at,updated_at) VALUES(?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))')
            .bind(seed.kid, agentId, JSON.stringify(seed.publicJwk), stamp, stamp).run();
        // Ten decided key requests created today by the agent's own key (for example a stolen one) exhaust the agent's add budget...
        const rejected = [];
        for (let n = 0; n < 10; n++) {
            const key = agentKey();
            rejected.push(h.db.prepare(`INSERT INTO key_requests(request_id,account_id,kid,public_jwk,endorsed_by,status,created_at,expires_at,updated_at,decided_at)
                VALUES(?,?,?,?,?,'rejected',CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER),CAST(? AS INTEGER))`)
                .bind(crypto.randomUUID(), agentId, key.kid, JSON.stringify(key.publicJwk), seed.kid, stamp, (BigInt(stamp) + dayTicks).toString(), stamp, stamp));
        }
        await h.db.batch(rejected);
        await problem(await addKey(h, {accountId: agentId, key: seed}, agentKey()), 429, 'capacity');
        // ...but not the owner-authorised recovery, which counts only key rows.
        const recover = async (key: TestKey) => {
            const issued = await issueRecoveryCode(env(h), {humanId, agentId});
            assert.equal(issued.ok, true);
            return h.mf.dispatchFetch(recoveryUrl, {method: 'POST', headers: {'Content-Type': 'application/json', DPoP: dpopProof(key, {method: 'POST', url: recoveryUrl})},
                body: JSON.stringify({agentId, code: (issued as {code: string}).code})}) as unknown as Promise<Response>;
        };
        const recovered = await recover(agentKey());
        assert.equal(recovered.status, 200, await recovered.clone().text());
        // Recovered keys are key additions too: ten key rows created today refuse the next recovery.
        await revokedRows(h, agentId, 8, nowTicks());
        assert.equal(await keyRows(h, agentId), 10);
        const blocked = agentKey();
        await problem(await recover(blocked), 429, 'capacity');
        assert.equal(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE kid=?').bind(blocked.kid).first('n'), 0);
    } finally { await h.close(); }
});
