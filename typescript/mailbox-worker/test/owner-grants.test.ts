import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { start } from './harness.ts';
import { createOwnedAgent, ownedAgentLimits, signInHuman } from '../src/humans.ts';
import { executeOwnerMcp } from '../src/owner-tools.ts';
import { maxLiveOwnerGrants, recordGrant, recordOwnerGrant, revokeGrant } from '../src/client-grants.ts';
import { ownerAction, ownerOverview } from '../src/owner-actions.ts';
import { type Database, type Env } from '../src/database.ts';
import { remoteMcp } from '../src/remote-mcp.ts';
import { maxLiveWebhooks, registerWebhook, revokeWebhook } from '../src/app-webhooks.ts';
import { ownerMcpTools } from '../src/private-mcp-tools.ts';
import { openWebhookSecret } from '../src/credential-vault.ts';
import { oauthConfiguration } from '../src/remote-oauth.ts';

// remote-private uses bundler-resolution imports; evaluate the bundled production module.
const built = await build({ entryPoints: ['src/remote-private.ts'], bundle: true, write: false, platform: 'node', format: 'esm', logLevel: 'silent' });
const { oauthPrivateAccess } = await import('data:text/javascript;base64,' + Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');

const origin = 'https://demo.test', resource = origin + '/mcp', clientId = 'https://office.example/oauth-client.json';
const manage = 'weave:agents.manage';
const environment = (db: Database, overrides: Partial<Env> = {}): Env => ({
    db, ...overrides });
let subjects = 800000;
async function human(env: Env) {
    const signed = await signInHuman(env, { provider: 'github', subject: String(++subjects), providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'owner' + subjects });
    assert.equal(signed.ok, true);
    return (signed as { humanId: string }).humanId;
}
async function agentOf(env: Env, humanId: string, displayName = 'Desk') {
    const created = await createOwnedAgent(env, { humanId, displayName, sourceIp: '203.0.113.9' });
    assert.equal(created.ok, true);
    return (created as { accountId: string }).accountId;
}
const ownerOf = (db: Database, accountId: string) => db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(accountId).first<string>('human_id');
const scopes = [manage, 'weave:addresses.read', 'weave:addresses.write'];
/** Owner-grant claims as the authorization server issues them: the human as the user, no account. */
const ownerClaims = (humanId: string, grantId: string, props: Record<string, unknown> = {}) => ({ audience: resource, userId: humanId,
    props: { owner: true, humanId, grantId, ...props }, expiresAt: Math.floor(Date.now() / 1000) + 120, scope: scopes, clientId });

type Call = { name: string; args?: Record<string, unknown>; agent?: string; afterAuthorize?: () => Promise<void> };
/** The production /mcp composition with Service Binding claims and the D1 grant rows read on every call. */
function mcp(env: Env, claims: () => unknown) {
    const cfg = oauthConfiguration({ OAUTH_ISSUER: 'https://auth.example', OAUTH_RESOURCE: resource,
        AUTH_SERVER: { validateToken: async () => claims() as any } }, resource)!;
    return async ({ name, args = {}, agent, afterAuthorize }: Call) => {
        const headers: Record<string, string> = { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream',
            Authorization: 'Bearer ' + crypto.randomUUID() + ':sdkgrant:secret', 'CF-Connecting-IP': '198.51.100.4' };
        if (agent !== undefined) headers['Weave-Agent'] = agent;
        const request = new Request(resource, { method: 'POST', headers, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name, arguments: args } }) });
        const access = oauthPrivateAccess(env, origin, cfg, request);
        return remoteMcp(request, async () => { throw Error('no public read'); }, { ...access,
            authorize: async scope => { const caller = await access.authorize(scope); await afterAuthorize?.(); return caller; } });
    };
}
const resultOf = async (response: Response) => { assert.equal(response.status, 200, await response.clone().text()); return (await response.json()).result; };

test('recordOwnerGrant_Human_RecordedFrozenAndUnknownRefused', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordOwnerGrant(env, { grantId, humanId, clientId, clientName: 'AI Office' }), { ok: true });
        assert.deepEqual(await h.db.prepare('SELECT human_id,client_id,client_name,revoked_at FROM owner_grants WHERE grant_id=?').bind(grantId).first(),
            { human_id: humanId, client_id: clientId, client_name: 'AI Office', revoked_at: null });
        assert.deepEqual(await recordOwnerGrant(env, { grantId: crypto.randomUUID(), humanId: crypto.randomUUID(), clientId }), { ok: false, reason: 'not-found' });
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(humanId).run();
        assert.deepEqual(await recordOwnerGrant(env, { grantId: crypto.randomUUID(), humanId, clientId }), { ok: false, reason: 'frozen' });
        assert.deepEqual(await recordOwnerGrant(environment(h.db, { RECOVERY_READ_ONLY: 'true' }), { grantId: crypto.randomUUID(), humanId, clientId }), { ok: false, reason: 'unavailable' });
    } finally { await h.close(); }
});

// A1-1: letting the owner grant's app approve or reject is off unless the consent asked for it, stored in the same transaction,
// shown on the owner overview, stopped on its own (keeping the grant) or with the grant, and never reachable for another human.
test('recordOwnerGrant_AppDecides_RecordedOnlyWhenChosenShownAndStoppable', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), other = await human(env);
        const plain = crypto.randomUUID(), deciding = crypto.randomUUID(), revoked = crypto.randomUUID();
        const row = (grantId: string) => h.db.prepare('SELECT human_id,revoked_at FROM owner_grant_approvals WHERE grant_id=?').bind(grantId).first();
        assert.deepEqual(await recordOwnerGrant(env, { grantId: plain, humanId, clientId }), { ok: true });
        assert.deepEqual(await recordOwnerGrant(env, { grantId: crypto.randomUUID(), humanId, clientId, appDecides: false }), { ok: true });
        assert.equal(await h.db.prepare('SELECT count(*) n FROM owner_grant_approvals').first('n'), 0, 'off unless chosen');
        assert.deepEqual(await recordOwnerGrant(env, { grantId: deciding, humanId, clientId, appDecides: true }), { ok: true });
        assert.deepEqual(await recordOwnerGrant(env, { grantId: revoked, humanId, clientId, appDecides: true }), { ok: true });
        assert.deepEqual(await row(deciding), { human_id: humanId, revoked_at: null });
        await assert.rejects(recordOwnerGrant(env, { grantId: crypto.randomUUID(), humanId, clientId, appDecides: 'yes' as unknown as boolean }));
        const shown = async () => {
            const overview = await ownerOverview(env, humanId);
            assert.equal(overview.frozen, false);
            return Object.fromEntries((overview as { ownerGrants: { grantId: string; appDecides: boolean }[] }).ownerGrants.map(g => [g.grantId, g.appDecides]));
        };
        const before = await shown();
        assert.equal(before[plain], false);
        assert.equal(before[deciding], true);
        const authAt = Date.now();
        // Another human's grant is indistinguishable from one without the permission.
        assert.deepEqual(await ownerAction(env, { humanId: other, authAt, kind: 'stop-app-decisions', grantId: deciding }), { ok: false, reason: 'not-found' });
        assert.deepEqual(await ownerAction(env, { humanId, authAt, kind: 'stop-app-decisions', grantId: plain }), { ok: false, reason: 'not-found' });
        assert.deepEqual(await row(deciding), { human_id: humanId, revoked_at: null });
        // Protective: an old sign-in may stop it; it is idempotent and keeps the grant live.
        assert.deepEqual(await ownerAction(env, { humanId, authAt: 0, kind: 'stop-app-decisions', grantId: deciding }), { ok: true });
        const stoppedAt = (await row(deciding) as { revoked_at: unknown }).revoked_at;
        assert.notEqual(stoppedAt, null);
        assert.deepEqual(await ownerAction(env, { humanId, authAt, kind: 'stop-app-decisions', grantId: deciding }), { ok: true });
        assert.deepEqual((await row(deciding) as { revoked_at: unknown }).revoked_at, stoppedAt, 'the first stop time is kept');
        assert.equal(await h.db.prepare('SELECT revoked_at FROM owner_grants WHERE grant_id=?').bind(deciding).first('revoked_at'), null);
        assert.equal((await shown())[deciding], false);
        // Revoking the grant ends its permission in the same transaction.
        assert.deepEqual(await revokeGrant(env, { humanId, grantId: revoked }), { ok: true });
        assert.notEqual((await row(revoked) as { revoked_at: unknown }).revoked_at, null);
        // A frozen human records no grant, so no permission either.
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(other).run();
        assert.deepEqual(await recordOwnerGrant(env, { grantId: crypto.randomUUID(), humanId: other, clientId, appDecides: true }), { ok: false, reason: 'frozen' });
        assert.equal(await h.db.prepare('SELECT count(*) n FROM owner_grant_approvals').first('n'), 2);
    } finally { await h.close(); }
});

test('recordOwnerGrant_LiveLimit_GrantLimitUntilOneIsRevoked', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), ids: string[] = [];
        for (let i = 0; i < maxLiveOwnerGrants; i++) { ids.push(crypto.randomUUID()); assert.deepEqual(await recordOwnerGrant(env, { grantId: ids[i]!, humanId, clientId }), { ok: true }); }
        assert.deepEqual(await recordOwnerGrant(env, { grantId: crypto.randomUUID(), humanId, clientId }), { ok: false, reason: 'grant-limit' });
        assert.deepEqual(await revokeGrant(env, { humanId, grantId: ids[0]! }), { ok: true });
        assert.deepEqual(await recordOwnerGrant(env, { grantId: crypto.randomUUID(), humanId, clientId }), { ok: true });
    } finally { await h.close(); }
});

test('ownerGrant_ManageTools_ListCreateRenameTheHumansAgentsOnly', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), other = await human(env), grantId = crypto.randomUUID();
        const first = await agentOf(env, humanId, 'First'), foreign = await agentOf(env, other, 'Foreign');
        assert.deepEqual(await recordOwnerGrant(env, { grantId, humanId, clientId }), { ok: true });
        const call = mcp(env, () => ownerClaims(humanId, grantId));
        const created = (await resultOf(await call({ name: 'create_agent', args: { displayName: 'Bookkeeper' } }))).structuredContent;
        assert.equal(created.displayName, 'Bookkeeper');
        assert.equal(await ownerOf(h.db, created.accountId), humanId);
        const listed = (await resultOf(await call({ name: 'list_agents' }))).structuredContent;
        assert.deepEqual(listed, { items: [{ accountId: first, displayName: 'First' }, { accountId: created.accountId, displayName: 'Bookkeeper' }], nextCursor: null });
        assert.deepEqual((await resultOf(await call({ name: 'rename_agent', args: { accountId: first, displayName: 'Front desk' } }))).structuredContent,
            { accountId: first, displayName: 'Front desk' });
        assert.equal(await h.db.prepare('SELECT display_name FROM accounts WHERE account_id=?').bind(first).first('display_name'), 'Front desk');
        const refused = await resultOf(await call({ name: 'rename_agent', args: { accountId: foreign, displayName: 'Mine now' } }));
        assert.deepEqual(refused.structuredContent, { status: 404, code: 'agent-not-found' });
        assert.equal(await h.db.prepare('SELECT display_name FROM accounts WHERE account_id=?').bind(foreign).first('display_name'), 'Foreign');
        const invisible = await resultOf(await call({ name: 'create_agent', args: { displayName: '​' } }));
        assert.equal(invisible.isError, true);
        assert.equal(invisible.structuredContent.field, 'displayName');
    } finally { await h.close(); }
});

test('ownerGrant_CreateAtTheLimit_AgentLimitCodeNothingWritten', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), grantId = crypto.randomUUID();
        await agentOf(env, humanId, 'Only');
        assert.deepEqual(await recordOwnerGrant(env, { grantId, humanId, clientId }), { ok: true });
        // The owner tool itself, with a limit of one agent per human instead of the deployed one.
        const refused = await executeOwnerMcp(env, humanId, 'create_agent', { displayName: 'Second' }, grantId, '198.51.100.4',
            { ...ownedAgentLimits, ownedPerHuman: 1 });
        assert.equal(refused.status, 409);
        assert.equal((await refused.json() as { code: string }).code, 'agent-limit');
        assert.equal(await h.db.prepare('SELECT count(*) AS n FROM accounts WHERE human_id=?').bind(humanId).first('n'), 1);
    } finally { await h.close(); }
});

test('ownerGrant_ActsAsTheNamedAgentOfItsHumanOnly', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), other = await human(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordOwnerGrant(env, { grantId, humanId, clientId }), { ok: true });
        // An agent the human owns after the grant was given is covered as well.
        const mine = await agentOf(env, humanId), foreign = await agentOf(env, other);
        const call = mcp(env, () => ownerClaims(humanId, grantId));
        assert.deepEqual((await resultOf(await call({ name: 'get_my_profile', agent: mine }))).structuredContent, { id: mine });
        const address = crypto.randomUUID();
        assert.equal((await resultOf(await call({ name: 'ensure_my_address', agent: mine, args: { addressId: address } }))).isError, undefined);
        assert.equal(await h.db.prepare('SELECT account_id FROM account_addresses WHERE address_id=?').bind(address).first('account_id'), mine);
        for (const agent of [undefined, foreign, crypto.randomUUID(), 'not-an-id']) {
            const refused = await resultOf(await call({ name: 'get_my_profile', ...(agent === undefined ? {} : { agent }) }));
            assert.equal(refused.isError, true, String(agent));
            assert.deepEqual(refused.structuredContent, { status: 400, code: 'invalid_agent' });
        }
        const managed = await resultOf(await call({ name: 'list_agents', agent: mine }));
        assert.deepEqual(managed.structuredContent, { status: 400, code: 'invalid_agent' }, 'management never names an agent');
        // An agent that leaves the human is no longer reachable through the grant.
        await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(other, mine).run();
        assert.deepEqual((await resultOf(await call({ name: 'get_my_profile', agent: mine }))).structuredContent, { status: 400, code: 'invalid_agent' });
    } finally { await h.close(); }
});

test('agentGrant_NeverManagesAgentsOrNamesAnother', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), agent = await agentOf(env, humanId), other = await agentOf(env, humanId), grantId = crypto.randomUUID();
        assert.deepEqual(await recordGrant(env, { grantId, humanId, accountId: agent, clientId }), { ok: true });
        const call = mcp(env, () => ({ audience: resource, userId: agent, props: { accountId: agent, humanId, grantId },
            expiresAt: Math.floor(Date.now() / 1000) + 120, scope: scopes, clientId }));
        const listed = await call({ name: 'list_agents' });
        assert.equal(listed.status, 403, 'an agent grant never holds the management scope, whatever its scope list says');
        assert.deepEqual((await resultOf(await call({ name: 'get_my_profile', agent: other }))).structuredContent, { status: 400, code: 'invalid_agent' });
        assert.deepEqual((await resultOf(await call({ name: 'get_my_profile' }))).structuredContent, { id: agent });
    } finally { await h.close(); }
});

test('ownerGrant_RevokedFrozenOrMixedClaims_401', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), agent = await agentOf(env, humanId), grantId = crypto.randomUUID();
        assert.deepEqual(await recordOwnerGrant(env, { grantId, humanId, clientId }), { ok: true });
        for (const claims of [ownerClaims(humanId, grantId, { accountId: agent }), { ...ownerClaims(humanId, grantId), userId: agent },
            { ...ownerClaims(humanId, grantId), clientId: 'https://other.example/client.json' }])
            assert.equal((await mcp(env, () => claims)({ name: 'list_agents' })).status, 401);
        // An owner grant presented as an agent grant does not match its row either.
        assert.equal((await mcp(env, () => ({ ...ownerClaims(humanId, grantId), userId: agent, props: { accountId: agent, humanId, grantId } }))({ name: 'get_my_profile' })).status, 401);
        const call = mcp(env, () => ownerClaims(humanId, grantId));
        assert.equal((await call({ name: 'list_agents' })).status, 200);
        await h.db.prepare('UPDATE humans SET frozen_at=updated_at WHERE human_id=?').bind(humanId).run();
        assert.equal((await call({ name: 'list_agents' })).status, 401);
        assert.equal((await call({ name: 'get_my_profile', agent })).status, 401);
        await h.db.prepare('UPDATE humans SET frozen_at=NULL WHERE human_id=?').bind(humanId).run();
        assert.deepEqual(await revokeGrant(env, { humanId, grantId }), { ok: true });
        assert.equal((await call({ name: 'list_agents' })).status, 401);
        assert.equal((await call({ name: 'get_my_profile', agent })).status, 401);
    } finally { await h.close(); }
});

test('ownerGrant_RevokedAfterAuthorization_WritesRefusedInTheirOwnTransaction', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), agent = await agentOf(env, humanId, 'Kept'), grantId = crypto.randomUUID();
        assert.deepEqual(await recordOwnerGrant(env, { grantId, humanId, clientId }), { ok: true });
        const call = mcp(env, () => ownerClaims(humanId, grantId));
        const revoke = async () => { assert.deepEqual(await revokeGrant(env, { humanId, grantId }), { ok: true }); };
        const created = await resultOf(await call({ name: 'create_agent', args: { displayName: 'Late' }, afterAuthorize: revoke }));
        assert.deepEqual(created.structuredContent, { status: 401, code: 'grant-revoked' });
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts WHERE human_id=?').bind(humanId).first('n'), 1);
        await h.db.prepare('UPDATE owner_grants SET revoked_at=NULL WHERE grant_id=?').bind(grantId).run();
        const renamed = await resultOf(await call({ name: 'rename_agent', args: { accountId: agent, displayName: 'Changed' }, afterAuthorize: revoke }));
        assert.deepEqual(renamed.structuredContent, { status: 401, code: 'grant-revoked' });
        assert.equal(await h.db.prepare('SELECT display_name FROM accounts WHERE account_id=?').bind(agent).first('display_name'), 'Kept');
        await h.db.prepare('UPDATE owner_grants SET revoked_at=NULL WHERE grant_id=?').bind(grantId).run();
        const address = await resultOf(await call({ name: 'ensure_my_address', agent, args: { addressId: crypto.randomUUID() }, afterAuthorize: revoke }));
        assert.deepEqual(address.structuredContent, { status: 401, code: 'grant-revoked' });
    } finally { await h.close(); }
});

test('ownerOverview_OwnerGrants_LiveListedRevokedForCleanUp', async () => {
    const h = await start();
    try {
        const env = environment(h.db), humanId = await human(env), live = crypto.randomUUID(), revoked = crypto.randomUUID();
        assert.deepEqual(await recordOwnerGrant(env, { grantId: live, humanId, clientId, clientName: 'AI Office' }), { ok: true });
        assert.deepEqual(await recordOwnerGrant(env, { grantId: revoked, humanId, clientId }), { ok: true });
        assert.deepEqual(await revokeGrant(env, { humanId, grantId: revoked }), { ok: true });
        const overview = await ownerOverview(env, humanId);
        if (overview.frozen) throw Error('frozen');
        assert.deepEqual(overview.ownerGrants.map(grant => [grant.grantId, grant.clientId, grant.clientName]), [[live, clientId, 'AI Office']]);
        assert.deepEqual(overview.revokedOwnerGrantIds, [revoked]);
    } finally { await h.close(); }
});

const vaultKey = Buffer.alloc(32, 7).toString('base64url');
test('ownerOverview_OwnerGrantWebhooks_LiveCallbacksListedPerLiveGrantOnly', async () => {
    const h = await start();
    try {
        const env = environment(h.db, { EXTERNAL_CREDENTIAL_KEY: vaultKey }), humanId = await human(env), otherHuman = await human(env);
        const [office, quiet, revoked, foreign] = [crypto.randomUUID(), crypto.randomUUID(), crypto.randomUUID(), crypto.randomUUID()];
        for (const [grantId, owner] of [[office, humanId], [quiet, humanId], [revoked, humanId], [foreign, otherHuman]])
            assert.deepEqual(await recordOwnerGrant(env, { grantId, humanId: owner, clientId }), { ok: true });
        const register = async (grantId: string, url: string) => {
            const result = await registerWebhook(env, { grantId, url }, async () => {});
            if (!result.ok) throw Error(result.reason);
            return result.webhook;
        };
        const first = await register(office, 'https://office.example/hooks/a'), gone = await register(office, 'https://office.example/hooks/gone');
        const second = await register(office, 'https://office.example/hooks/b');
        assert.deepEqual(await revokeWebhook(env, { grantId: office, webhookId: gone.webhookId }, async () => {}), { ok: true });
        await register(revoked, 'https://revoked.example/hooks');
        await register(foreign, 'https://foreign.example/hooks');
        assert.deepEqual(await revokeGrant(env, { humanId, grantId: revoked }), { ok: true });

        const overview = await ownerOverview(env, humanId);
        if (overview.frozen) throw Error('frozen');
        const byGrant = Object.fromEntries(overview.ownerGrants.map(grant => [grant.grantId, grant.webhooks]));
        assert.deepEqual(Object.keys(byGrant).sort(), [office, quiet].sort(), 'a revoked grant is not listed');
        const plain = (w: { webhookId: string; url: string; createdAt: string }) => ({ ...w, createdAt: Date.parse(w.createdAt) });
        assert.deepEqual(byGrant[office].map(plain), [first, second].map(plain), 'live callbacks of that grant, oldest first, without secrets');
        assert.deepEqual(byGrant[quiet], []);
        const other = await ownerOverview(env, otherHuman);
        if (other.frozen) throw Error('frozen');
        assert.deepEqual(other.ownerGrants.map(grant => grant.webhooks.map(w => w.url)), [['https://foreign.example/hooks']], 'another human sees only their own');
    } finally { await h.close(); }
});

test('ownerGrant_Webhooks_RegisterListRevokeSecretOnceSealedAndGrantScoped', async () => {
    const h = await start();
    try {
        const env = environment(h.db, { EXTERNAL_CREDENTIAL_KEY: vaultKey }), humanId = await human(env), grantId = crypto.randomUUID(), otherGrant = crypto.randomUUID();
        assert.deepEqual(await recordOwnerGrant(env, { grantId, humanId, clientId }), { ok: true });
        assert.deepEqual(await recordOwnerGrant(env, { grantId: otherGrant, humanId, clientId }), { ok: true });
        const call = mcp(env, () => ownerClaims(humanId, grantId)), other = mcp(env, () => ownerClaims(humanId, otherGrant));
        const registered = (await resultOf(await call({ name: 'register_webhook', args: { url: 'https://Office.Example/hooks/weave?x=1' } }))).structuredContent;
        assert.equal(registered.url, 'https://office.example/hooks/weave?x=1');
        assert.match(registered.secret, /^[A-Za-z0-9_-]{43}$/);
        const row = await h.db.prepare('SELECT grant_id,url,secret_sealed,revoked_at FROM app_webhooks WHERE webhook_id=?').bind(registered.webhookId)
            .first<{ grant_id: string; url: string; secret_sealed: ArrayBuffer; revoked_at: null }>();
        assert.equal(row!.grant_id, grantId);
        assert.equal(row!.revoked_at, null);
        const sealed = new Uint8Array(row!.secret_sealed);
        assert.equal(Buffer.from(sealed).includes(Buffer.from(registered.secret)), false);
        assert.equal(await openWebhookSecret(vaultKey, registered.webhookId, grantId, sealed), registered.secret);
        await assert.rejects(openWebhookSecret(vaultKey, registered.webhookId, otherGrant, sealed));
        const listed = (await resultOf(await call({ name: 'list_webhooks' }))).structuredContent;
        assert.deepEqual(listed, { items: [{ webhookId: registered.webhookId, url: registered.url, createdAt: registered.createdAt }], nextCursor: null });
        assert.equal(JSON.stringify(listed).includes(registered.secret), false);
        // Another grant of the same human neither sees nor revokes it.
        assert.deepEqual((await resultOf(await other({ name: 'list_webhooks' }))).structuredContent, { items: [], nextCursor: null });
        assert.deepEqual((await resultOf(await other({ name: 'revoke_webhook', args: { webhookId: registered.webhookId } }))).structuredContent,
            { status: 404, code: 'webhook-not-found' });
        assert.equal(await h.db.prepare('SELECT revoked_at FROM app_webhooks WHERE webhook_id=?').bind(registered.webhookId).first('revoked_at'), null);
        for (let i = 0; i < 2; i++)
            assert.deepEqual((await resultOf(await call({ name: 'revoke_webhook', args: { webhookId: registered.webhookId } }))).structuredContent,
                { webhookId: registered.webhookId, revoked: true });
        assert.notEqual(await h.db.prepare('SELECT revoked_at FROM app_webhooks WHERE webhook_id=?').bind(registered.webhookId).first('revoked_at'), null);
        assert.deepEqual((await resultOf(await call({ name: 'list_webhooks' }))).structuredContent, { items: [], nextCursor: null });
        // A revocation committed after authorization is refused inside the write's own transaction.
        const late = await resultOf(await call({ name: 'register_webhook', args: { url: 'https://office.example/late' },
            afterAuthorize: async () => { assert.deepEqual(await revokeGrant(env, { humanId, grantId }), { ok: true }); } }));
        assert.deepEqual(late.structuredContent, { status: 401, code: 'grant-revoked' });
        assert.equal((await call({ name: 'register_webhook', args: { url: 'https://office.example/again' } })).status, 401);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM app_webhooks').first('n'), 1);
    } finally { await h.close(); }
});

test('ownerGrant_RegisterWebhook_InvalidUrlLimitAndNoVaultKeyRefusedNothingWritten', async () => {
    const h = await start();
    try {
        const env = environment(h.db, { EXTERNAL_CREDENTIAL_KEY: vaultKey }), humanId = await human(env), grantId = crypto.randomUUID();
        assert.deepEqual(await recordOwnerGrant(env, { grantId, humanId, clientId }), { ok: true });
        const call = mcp(env, () => ownerClaims(humanId, grantId));
        for (const url of ['http://office.example/hook', 'https://user:pw@office.example/hook', 'https://user@office.example/hook', 'https://:pw@office.example/hook', 'https://office.example/hook#frag', 'https://office.example/hook#',
            'not a url', '/relative', 'https://127.0.0.1/hook', 'https://[::1]/hook', 'https://0x7f.1/hook', 'https://localhost/hook', 'https://a.localhost./hook', 'https://2130706433/hook', 'https://office.example/' + 'a'.repeat(2048), 42]) {
            const refused = await resultOf(await call({ name: 'register_webhook', args: { url } }));
            assert.equal(refused.isError, true, String(url));
            assert.equal(refused.structuredContent.field, 'url', String(url));
        }
        assert.equal(await h.db.prepare('SELECT count(*) n FROM app_webhooks').first('n'), 0);
        for (let i = 0; i < maxLiveWebhooks; i++)
            assert.equal((await resultOf(await call({ name: 'register_webhook', args: { url: 'https://office.example/' + i } }))).isError, undefined);
        const over = await resultOf(await call({ name: 'register_webhook', args: { url: 'https://office.example/over' } }))
        assert.deepEqual(over.structuredContent,
            { status: 409, code: 'webhook-limit' });
        assert.equal(await h.db.prepare('SELECT count(*) n FROM app_webhooks').first('n'), maxLiveWebhooks);
        assert.match(ownerMcpTools.find(t => t.name === 'register_webhook')!.description, new RegExp(`At most ${maxLiveWebhooks} live callbacks`));
        const first = (await resultOf(await call({ name: 'list_webhooks' }))).structuredContent.items[0].webhookId;
        await resultOf(await call({ name: 'revoke_webhook', args: { webhookId: first } }));
        assert.equal((await resultOf(await call({ name: 'register_webhook', args: { url: 'https://office.example/after' } }))).isError, undefined);
        const keyless = await resultOf(await mcp(environment(h.db), () => ownerClaims(humanId, grantId))({ name: 'register_webhook', args: { url: 'https://office.example/k' } }));
        assert.equal(keyless.isError, true);
        assert.match(keyless.content[0].text, /HTTP 503/);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM app_webhooks').first('n'), maxLiveWebhooks + 1);
    } finally { await h.close(); }
});
