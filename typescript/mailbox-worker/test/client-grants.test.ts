import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { start } from './harness.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import { liveOwnedGrant, recordGrant, revokeGrant } from '../src/client-grants.ts';
import { ownerAction } from '../src/owner-actions.ts';
import { atomic, type Database, type Env } from '../src/database.ts';
import { remoteMcp } from '../src/remote-mcp.ts';
import { oauthConfiguration } from '../src/remote-oauth.ts';

// remote-private uses bundler-resolution imports; evaluate the bundled production module.
const built = await build({ entryPoints: ['src/remote-private.ts'], bundle: true, write: false, platform: 'node', format: 'esm', logLevel: 'silent' });
const { oauthPrivateAccess, executePrivateMcp } = await import('data:text/javascript;base64,' + Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');

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
        assert.deepEqual(await liveOwnedGrant(h.db, grantId), { grantId, humanId, accountId, clientId });
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
        assert.equal(await liveOwnedGrant(h.db, grantId), null);
    } finally { await h.close(); }
});

type McpOptions = { lookup?: (grantId: string) => Promise<any>; name?: string; args?: Record<string, unknown>;
    afterAuthorize?: () => Promise<void>; invoked?: { count: number } };
/** The production /mcp composition: Service Binding claims, then the D1 grant row on every call and inside every write. */
function mcp(env: Env, claims: (token: string) => unknown, options: McpOptions = {}) {
    const cfg = oauthConfiguration({ OAUTH_ISSUER: 'https://auth.example', OAUTH_RESOURCE: resource,
        AUTH_SERVER: { validateToken: async (_resource: string, token: string) => claims(token) as any } }, resource)!;
    return async (token: string) => {
        const request = new Request(resource, { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', Authorization: 'Bearer ' + token },
            body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: options.name ?? 'get_my_profile', arguments: options.args ?? {} } }) });
        const access = oauthPrivateAccess(env, origin, cfg, request, options.lookup);
        return remoteMcp(request, async () => { throw Error('no public read'); }, { ...access,
            authorize: async scope => { const caller = await access.authorize(scope); await options.afterAuthorize?.(); return caller; },
            invoke: (accountId, name, args) => { if (options.invoked) options.invoked.count++; return access.invoke(accountId, name, args); } });
    };
}
const claim = (accountId: string, props: Record<string, unknown>, client = clientId, scope = ['weave:addresses.read']) => ({ audience: resource, userId: accountId,
    props: { accountId, ...props }, expiresAt: Math.floor(Date.now() / 1000) + 120, scope, clientId: client });
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

test('mcpPrivate_OwnerFrozenAfterGrant_401NextCall', async () => {
    // An already issued token is rechecked at the resource on every call: freezing the
    // human ends it without a revocation, and the agent's other owners are unaffected.
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), bob = await owner(env);
        const aliceGrant = crypto.randomUUID(), bobGrant = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId: aliceGrant, ...alice, clientId }), { ok: true });
        assert.deepEqual(await recordGrant(env, { grantId: bobGrant, ...bob, clientId }), { ok: true });
        const aliceCall = mcp(env, () => claim(alice.accountId, { humanId: alice.humanId, grantId: aliceGrant }));
        const bobCall = mcp(env, () => claim(bob.accountId, { humanId: bob.humanId, grantId: bobGrant }));
        assert.equal((await aliceCall(token(alice.accountId))).status, 200, 'positive control');
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(alice.humanId).run();
        const denied = await aliceCall(token(alice.accountId));
        assert.equal(denied.status, 401, "a frozen human's still-unexpired token must stop working");
        assert.match(denied.headers.get('WWW-Authenticate') ?? '', /error="invalid_token"/);
        assert.equal(await liveOwnedGrant(h.db, aliceGrant), null);
        assert.equal((await bobCall(token(bob.accountId))).status, 200, 'an unrelated human keeps access');
    } finally { await h.close(); }
});

test('liveOwnedGrant_AgentNowOwnedByAnotherHuman_Null', async () => {
    // A grant names the human who consented. If the agent later belongs to someone
    // else (or to no one), that human's grant must lose all authority.
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), bob = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        assert.deepEqual(await liveOwnedGrant(h.db, grantId), { grantId, ...alice, clientId });
        await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(bob.humanId, alice.accountId).run();
        assert.equal(await liveOwnedGrant(h.db, grantId), null, "the previous owner's grant is not live");
        await h.db.prepare('UPDATE accounts SET human_id=NULL WHERE account_id=?').bind(alice.accountId).run();
        assert.equal(await liveOwnedGrant(h.db, grantId), null, 'an ownerless agent accepts no human grant');
        await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(alice.humanId, alice.accountId).run();
        assert.deepEqual(await liveOwnedGrant(h.db, grantId), { grantId, ...alice, clientId }, 'positive control');
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
        let lookups = 0;
        const invoked = { count: 0 };
        const call = mcp(environment(broken), () => claim(alice.accountId, { humanId: alice.humanId, grantId }), { invoked });
        const failed = await call(token(alice.accountId));
        assert.equal(failed.status, 503);
        assert.equal(failed.headers.get('WWW-Authenticate'), null);
        assert.doesNotMatch(await failed.text(), /D1 unavailable/);
        const thrown = await mcp(env, () => claim(alice.accountId, { humanId: alice.humanId, grantId }),
            { lookup: async () => { lookups++; throw Error('storage'); }, invoked })(token(alice.accountId));
        assert.equal(thrown.status, 503);
        assert.equal(lookups, 1);
        assert.equal(invoked.count, 0, 'no tool runs when the grant cannot be read');
    } finally { await h.close(); }
});

test('mcpPrivate_TokenWithoutGrantId_401', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        let lookups = 0;
        for (const props of [{}, { humanId: alice.humanId }, { grantId }, { humanId: alice.humanId, grantId: 'not-a-uuid' }, { humanId: 'not-a-uuid', grantId }]) {
            const denied = await mcp(env, () => claim(alice.accountId, props), { lookup: async () => { lookups++; return null; } })(token(alice.accountId));
            assert.equal(denied.status, 401, JSON.stringify(props));
        }
        assert.equal(lookups, 0, 'a token without grant identity never reaches storage');
    } finally { await h.close(); }
});

test('mcpPrivate_GrantLookup_SinglePointReadWithoutTransaction', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        const statements: string[] = [];
        const counting: Database = { batch: s => h.db.batch(s), prepare: sql => { statements.push(sql); return h.db.prepare(sql); } };
        const allowed = await mcp(environment(counting), () => claim(alice.accountId, { humanId: alice.humanId, grantId }))(token(alice.accountId));
        assert.equal(allowed.status, 200);
        assert.equal(statements.filter(sql => sql.includes('client_grants')).length, 1);
        assert.equal(statements.filter(sql => sql.includes('mailbox_revision')).length, 0, 'unrelated revision churn cannot retry or fail the grant read');
    } finally { await h.close(); }
});

test('mcpPrivate_GrantRevokedBetweenAuthorizeAndWrite_NothingWritten', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        const claims = () => claim(alice.accountId, { humanId: alice.humanId, grantId }, clientId, ['weave:addresses.write']);
        const addresses = () => h.db.prepare('SELECT address_id FROM account_addresses WHERE account_id=?').bind(alice.accountId).all<{ address_id: string }>();
        const control = await mcp(env, claims, { name: 'ensure_my_address', args: { addressId: crypto.randomUUID() } })(token(alice.accountId));
        assert.equal((await control.json()).result.isError, undefined, 'positive control');
        const before = (await addresses()).results;
        const invoked = { count: 0 };
        const answer = await mcp(env, claims, { name: 'ensure_my_address', args: { addressId: crypto.randomUUID() }, invoked,
            afterAuthorize: async () => { assert.deepEqual(await revokeGrant(env, { humanId: alice.humanId, grantId }), { ok: true }); } })(token(alice.accountId));
        assert.equal(invoked.count, 1, 'the revocation landed after authorization, before the write');
        const result = (await answer.json()).result;
        assert.equal(result.isError, true);
        assert.deepEqual(result.structuredContent, { status: 401, code: 'grant-revoked' });
        assert.match(result.content[0].text, /revoked/);
        assert.deepEqual((await addresses()).results, before, 'nothing was written');
    } finally { await h.close(); }
});

test('mcpPrivate_OwnerFrozenBetweenAuthorizeAndWrite_NothingWritten', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        const claims = () => claim(alice.accountId, { humanId: alice.humanId, grantId }, clientId, ['weave:addresses.write']);
        const addresses = () => h.db.prepare('SELECT address_id FROM account_addresses WHERE account_id=?').bind(alice.accountId).all<{ address_id: string }>();
        const control = await mcp(env, claims, { name: 'ensure_my_address', args: { addressId: crypto.randomUUID() } })(token(alice.accountId));
        assert.equal((await control.json()).result.isError, undefined, 'positive control');
        const before = (await addresses()).results, beforeRevision = await revision(h.db);
        const invoked = { count: 0 };
        const answer = await mcp(env, claims, { name: 'ensure_my_address', args: { addressId: crypto.randomUUID() }, invoked,
            afterAuthorize: async () => { await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(alice.humanId).run(); } })(token(alice.accountId));
        assert.equal(invoked.count, 1, 'the freeze landed after authorization, before the write');
        const result = (await answer.json()).result;
        assert.equal(result.isError, true);
        assert.deepEqual(result.structuredContent, { status: 401, code: 'grant-revoked' });
        assert.deepEqual((await addresses()).results, before, 'nothing was written');
        assert.equal(await revision(h.db), beforeRevision, 'no state change was committed');
        assert.equal((await grantRow(h.db, grantId))?.revoked_at, null, 'the freeze fences the write without revoking the grant');
    } finally { await h.close(); }
});

test('mcpPrivate_RevokedTokenWithInsufficientScope_401', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        const call = mcp(env, () => claim(alice.accountId, { humanId: alice.humanId, grantId }), { name: 'ensure_my_address', args: { addressId: crypto.randomUUID() } });
        assert.equal((await call(token(alice.accountId))).status, 403, 'positive control: live grant, missing scope');
        assert.deepEqual(await revokeGrant(env, { humanId: alice.humanId, grantId }), { ok: true });
        const denied = await call(token(alice.accountId));
        assert.equal(denied.status, 401);
        assert.match(denied.headers.get('WWW-Authenticate') ?? '', /error="invalid_token"/);
    } finally { await h.close(); }
});

test('executePrivateMcp_WriteWithoutGrantIdentity_RefusedNothingWritten', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId }), { ok: true });
        const addresses = async () => (await h.db.prepare('SELECT count(*) n FROM account_addresses WHERE account_id=?').bind(alice.accountId).first<number>('n'));
        for (const missing of ['', undefined, null]) {
            const refused = await executePrivateMcp(env, origin, alice.accountId, 'ensure_my_address', { addressId: crypto.randomUUID() }, missing as unknown as string);
            assert.equal(refused.status, 401, String(missing));
            assert.equal((await refused.json()).code, 'grant-revoked');
        }
        assert.equal(await addresses(), 0, 'nothing was written');
        const other = await owner(env), foreignGrant = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId: foreignGrant, ...other, clientId }), { ok: true });
        assert.equal((await executePrivateMcp(env, origin, alice.accountId, 'ensure_my_address', { addressId: crypto.randomUUID() }, foreignGrant)).status, 401, "another account's grant");
        assert.equal((await executePrivateMcp(env, origin, alice.accountId, 'ensure_my_address', { addressId: crypto.randomUUID() }, grantId)).status, 200, 'positive control');
        assert.equal(await addresses(), 1);
    } finally { await h.close(); }
});

test('recordGrant_ClientNameStoredBounded', async () => {
    const h = await start();
    try {
        const env = environment(h.db), { humanId, accountId } = await owner(env);
        const name = (grantId: string) => h.db.prepare('SELECT client_name n FROM client_grants WHERE grant_id=?').bind(grantId).first<string | null>('n');
        const cases: [string | null | undefined, string | null][] = [['Claude Desktop', 'Claude Desktop'], [null, null], [undefined, null], ['', null],
            ['a\nb\u0000c\u009fd', 'a?b?c?d'], ['x'.repeat(200), 'x'.repeat(128)], ['   ', null], ['\t\n', null],
            ['a\u202ab\u202ec\u2066d\u2069e', 'a?b?c?d?e'], ['\u{1F600}'.repeat(130), '\u{1F600}'.repeat(128)],
            // The invisible characters the consent page strips; the emoji zero-width joiner is kept.
            ['Claude\u200bDesktop', 'Claude?Desktop'], ['a\u00adb\u061cc\u200ed\u200fe\u2060f\ufeffg', 'a?b?c?d?e?f?g'], ['\u{1F469}\u200d\u{1F52C}', '\u{1F469}\u200d\u{1F52C}']];
        for (const [clientName, stored] of cases) {
            const grantId = crypto.randomUUID();
            assert.deepEqual(await recordGrant(env, { grantId, humanId, accountId, clientId, ...(clientName === undefined ? {} : { clientName }) }), { ok: true });
            assert.equal(await name(grantId), stored, String(clientName));
        }
        await assert.rejects(recordGrant(env, { grantId: crypto.randomUUID(), humanId, accountId, clientId, clientName: 5 as unknown as string }));
    } finally { await h.close(); }
});

test('revokeGrantAction_Own_RevokedAndNextMcpCall401', async () => {
    const h = await start();
    try {
        const env = environment(h.db), alice = await owner(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, ...alice, clientId, clientName: 'Client' }), { ok: true });
        const call = mcp(env, () => claim(alice.accountId, { humanId: alice.humanId, grantId }));
        assert.equal((await call(token(alice.accountId))).status, 200);
        assert.deepEqual(await ownerAction(env, { kind: 'revoke-grant', humanId: alice.humanId, authAt: 0, grantId }), { ok: true });
        assert.equal((await call(token(alice.accountId))).status, 401);
    } finally { await h.close(); }
});
