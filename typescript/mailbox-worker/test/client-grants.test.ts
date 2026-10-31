import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { start } from './harness.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import { liveGrant, recordGrant, revokeGrant } from '../src/client-grants.ts';
import { atomic, type Database, type Env } from '../src/database.ts';
import { remoteMcp } from '../src/remote-mcp.ts';
import { authorizeOAuth, oauthConfiguration } from '../src/remote-oauth.ts';

// remote-private uses bundler-resolution imports; evaluate the bundled production module.
const built = await build({ entryPoints: ['src/remote-private.ts'], bundle: true, write: false, platform: 'node', format: 'esm', logLevel: 'silent' });
const { privateMcpTools, privateScope, executePrivateMcp } = await import('data:text/javascript;base64,' + Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');

const origin = 'https://demo.test', resource = origin + '/mcp', clientId = 'https://client.example/oauth-client.json';
const environment = (db: Database, overrides: Partial<Env> = {}): Env => ({
    db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
    REGISTRATION_GLOBAL_PER_HOUR: '100', CLEANUP_MAX_MESSAGES: '0', CLEANUP_TIME_BUDGET_MS: '0', ...overrides });
let subjects = 700000;
async function owner(env: Env) {
    const human = await signInHuman(env, { provider: 'github', subject: String(++subjects), providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'owner' + subjects });
    assert.equal(human.ok, true);
    const humanId = (human as { humanId: string }).humanId;
    const agent = await createOwnedAgent(env, { humanId, displayName: 'Agent', sourceIp: '203.0.113.9' });
    assert.equal(agent.ok, true);
    return { humanId, accountId: (agent as { accountId: string }).accountId };
}
const revision = (db: Database) => db.prepare('SELECT token FROM mailbox_revision WHERE singleton=1').first('token');
const grantRow = (db: Database, grantId: string) => db.prepare('SELECT human_id,account_id,client_id,CAST(revoked_at AS TEXT) revoked_at FROM client_grants WHERE grant_id=?')
    .bind(grantId).first<{ human_id: string; account_id: string; client_id: string; revoked_at: string | null }>();

test('recordGrant_OwnedAgent_Recorded', async () => {
    const h = await start();
    try {
        const env = environment(h.db), { humanId, accountId } = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, humanId, accountId, clientId }, () => 1234n), { ok: true });
        assert.deepEqual(await grantRow(h.db, grantId), { human_id: humanId, account_id: accountId, client_id: clientId, revoked_at: null });
        assert.equal(await h.db.prepare('SELECT CAST(created_at AS TEXT) t FROM client_grants WHERE grant_id=?').bind(grantId).first('t'), '1234');
        assert.deepEqual(await atomic(h.db, tx => liveGrant(tx, grantId)), { grantId, humanId, accountId, clientId });
    } finally { await h.close(); }
});

test('recordGrant_NotOwnersAgent_NotFound', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), bob = await owner(env), unbound = await h.register();
        const before = await revision(h.db);
        for (const accountId of [bob.accountId, unbound.accountId, crypto.randomUUID()])
            assert.deepEqual(await recordGrant(env, { grantId: crypto.randomUUID(), humanId: alice.humanId, accountId, clientId }), { ok: false, reason: 'not-found' });
        assert.deepEqual(await recordGrant(env, { grantId: crypto.randomUUID(), humanId: crypto.randomUUID(), accountId: alice.accountId, clientId }), { ok: false, reason: 'not-found' });
        assert.equal(await h.db.prepare('SELECT count(*) n FROM client_grants').first('n'), 0);
        assert.equal(await revision(h.db), before);
    } finally { await h.close(); }
});

test('recordGrant_Frozen_Frozen', async () => {
    const h = await start();
    try {
        const env = environment(h.db), { humanId, accountId } = await owner(env);
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(humanId).run();
        assert.deepEqual(await recordGrant(env, { grantId: crypto.randomUUID(), humanId, accountId, clientId }), { ok: false, reason: 'frozen' });
        assert.equal(await h.db.prepare('SELECT count(*) n FROM client_grants').first('n'), 0);
    } finally { await h.close(); }
});

test('recordGrant_and_revokeGrant_MaintenanceOrReadOnly_UnavailableNoWrite', async () => {
    const h = await start();
    try {
        const env = environment(h.db), { humanId, accountId } = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, humanId, accountId, clientId }), { ok: true });
        for (const gate of [{ MAINTENANCE_MODE: 'true' }, { RECOVERY_READ_ONLY: 'true' }]) {
            const closed = environment(h.db, gate);
            assert.deepEqual(await recordGrant(closed, { grantId: crypto.randomUUID(), humanId, accountId, clientId }), { ok: false, reason: 'unavailable' });
            assert.deepEqual(await revokeGrant(closed, { humanId, grantId }), { ok: false, reason: 'unavailable' });
        }
        assert.equal(await h.db.prepare('SELECT count(*) n FROM client_grants').first('n'), 1);
        assert.equal((await grantRow(h.db, grantId))?.revoked_at, null);
    } finally { await h.close(); }
});

test('revokeGrant_OtherHuman_NotFoundNoChange', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), bob = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        const before = await revision(h.db);
        assert.deepEqual(await revokeGrant(env, { humanId: bob.humanId, grantId }), { ok: false, reason: 'not-found' });
        assert.deepEqual(await revokeGrant(env, { humanId: alice.humanId, grantId: crypto.randomUUID() }), { ok: false, reason: 'not-found' });
        assert.deepEqual(await revokeGrant(env, { humanId: alice.humanId, grantId: 'not-a-grant' }), { ok: false, reason: 'not-found' });
        assert.equal((await grantRow(h.db, grantId))?.revoked_at, null);
        assert.equal(await revision(h.db), before);
    } finally { await h.close(); }
});

test('revokeGrant_Twice_Ok', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }, () => 100n), { ok: true });
        assert.deepEqual(await revokeGrant(env, { humanId: alice.humanId, grantId }, () => 200n), { ok: true });
        const revoked = await revision(h.db);
        assert.deepEqual(await revokeGrant(env, { humanId: alice.humanId, grantId }, () => 300n), { ok: true });
        assert.equal((await grantRow(h.db, grantId))?.revoked_at, '200', 'the first revocation time is kept');
        assert.equal(await revision(h.db), revoked, 'a repeated revocation writes nothing');
        assert.equal(await atomic(h.db, tx => liveGrant(tx, grantId)), null);
    } finally { await h.close(); }
});

/** The production /mcp composition: Service Binding claims, then the D1 grant row on every call. */
function mcp(env: Env, claims: (token: string) => unknown, lookup = (grantId: string) => atomic(env.db, tx => liveGrant(tx, grantId))) {
    const cfg = oauthConfiguration({ OAUTH_ISSUER: 'https://auth.example', OAUTH_RESOURCE: resource,
        AUTH_SERVER: { validateToken: async (_resource: string, token: string) => claims(token) as any } }, resource)!;
    return async (token: string) => {
        const request = new Request(resource, { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', Authorization: 'Bearer ' + token },
            body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'get_my_profile', arguments: {} } }) });
        return remoteMcp(request, async () => { throw Error('no public read'); }, { tools: privateMcpTools, scope: privateScope,
            authorize: async scope => { const access = await authorizeOAuth(cfg, request, scope, lookup); return access instanceof Response ? access : access.accountId; },
            invoke: (accountId, name, args) => executePrivateMcp(env, origin, accountId, name, args) });
    };
}
const claim = (accountId: string, props: Record<string, unknown>, client = clientId) => ({ audience: resource, userId: accountId, props: { accountId, ...props },
    expiresAt: Math.floor(Date.now() / 1000) + 120, scope: ['weave:addresses.read'], clientId: client });
const token = (accountId: string) => accountId + ':sdkgrant:secret';

test('mcpPrivate_RevokedGrant_401NextCall', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        // The token is minted before the revocation and stays valid at the authorization server.
        const call = mcp(env, () => claim(alice.accountId, { humanId: alice.humanId, grantId }));
        const allowed = await call(token(alice.accountId));
        assert.equal(allowed.status, 200);
        assert.deepEqual((await allowed.json()).result.structuredContent, { id: alice.accountId });
        assert.deepEqual(await revokeGrant(env, { humanId: alice.humanId, grantId }), { ok: true });
        const denied = await call(token(alice.accountId));
        assert.equal(denied.status, 401);
        assert.match(denied.headers.get('WWW-Authenticate') ?? '', /error="invalid_token"/);
    } finally { await h.close(); }
});

test('mcpPrivate_GrantRowMismatch_401', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), bob = await owner(env), grantId = crypto.randomUUID();
        const second = await createOwnedAgent(env, { humanId: alice.humanId, displayName: 'Second', sourceIp: '203.0.113.9' });
        assert.equal(second.ok, true);
        const otherAgent = (second as { accountId: string }).accountId;
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        for (const claims of [
            claim(otherAgent, { humanId: alice.humanId, grantId }),
            claim(alice.accountId, { humanId: bob.humanId, grantId }),
            claim(alice.accountId, { humanId: alice.humanId, grantId }, 'https://other-client.example/client.json'),
            claim(alice.accountId, { humanId: alice.humanId, grantId: crypto.randomUUID() })
        ]) {
            const denied = await mcp(env, () => claims)(token(claims.userId));
            assert.equal(denied.status, 401, JSON.stringify(claims));
        }
        assert.equal((await mcp(env, () => claim(alice.accountId, { humanId: alice.humanId, grantId }))(token(alice.accountId))).status, 200, 'positive control');
    } finally { await h.close(); }
});

test('mcpPrivate_GrantStorageFailure_503', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        const broken: Database = { batch: s => h.db.batch(s), prepare: sql => { if (sql.includes('client_grants')) throw Error('D1 unavailable'); return h.db.prepare(sql); } };
        let invoked = 0;
        const call = mcp(environment(broken), () => claim(alice.accountId, { humanId: alice.humanId, grantId }));
        const failed = await call(token(alice.accountId));
        assert.equal(failed.status, 503);
        assert.equal(failed.headers.get('WWW-Authenticate'), null);
        assert.doesNotMatch(await failed.text(), /D1 unavailable/);
        const thrown = await mcp(env, () => claim(alice.accountId, { humanId: alice.humanId, grantId }), async () => { invoked++; throw Error('storage'); })(token(alice.accountId));
        assert.equal(thrown.status, 503);
        assert.equal(invoked, 1);
    } finally { await h.close(); }
});

test('mcpPrivate_TokenWithoutGrantId_401', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        let lookups = 0;
        for (const props of [{}, { humanId: alice.humanId }, { grantId }, { humanId: alice.humanId, grantId: 'not-a-uuid' }, { humanId: 'not-a-uuid', grantId }]) {
            const denied = await mcp(env, () => claim(alice.accountId, props), async () => { lookups++; return null; })(token(alice.accountId));
            assert.equal(denied.status, 401, JSON.stringify(props));
        }
        assert.equal(lookups, 0, 'a token without grant identity never reaches storage');
    } finally { await h.close(); }
});
