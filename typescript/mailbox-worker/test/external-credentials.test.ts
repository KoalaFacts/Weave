import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { connectExternalCredential, type ConnectCredentialInput } from '../src/external-credentials.ts';
import { openSecret } from '../src/credential-vault.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const key = randomBytes(32).toString('base64url');
const channelId = 'UC' + 'a'.repeat(22);
const scope = 'https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly';
const environment = (h: Harness, overrides: Partial<Env> = {}) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100',
    REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100', GOOGLE_CLIENT_ID: 'client.apps.googleusercontent.com',
    GOOGLE_CLIENT_SECRET: 'google-secret', EXTERNAL_CREDENTIAL_KEY: key, ...overrides}) as unknown as Env;

type Google = {token: () => Response | Promise<Response>; channels: () => Response | Promise<Response>; requests: {url: string; body: string; auth: string | null}[]};
/** Replaces fetch for the fixed Google endpoints while `run` executes; any other URL is refused. */
async function withGoogle(google: Partial<Google>, run: (requests: Google['requests']) => Promise<void>) {
    const requests: Google['requests'] = [], original = globalThis.fetch;
    const token = google.token ?? (() => Response.json({access_token: 'ya29.access', refresh_token: '1//refresh-' + randomBytes(4).toString('hex'), scope, token_type: 'Bearer'}));
    const channels = google.channels ?? (() => Response.json({items: [{id: channelId, snippet: {title: 'HSC Beats'}}]}));
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input instanceof Request ? input.url : input);
        requests.push({url, body: init?.body ? String(init.body) : '', auth: new Headers(init?.headers).get('Authorization')});
        assert.equal(init?.redirect, 'manual', 'never follow a provider redirect with credentials');
        if (url === 'https://oauth2.googleapis.com/token') return token();
        if (url.startsWith('https://www.googleapis.com/youtube/v3/channels?')) return channels();
        return new Response('refused', {status: 599});
    };
    try { await run(requests); } finally { globalThis.fetch = original; }
}

async function owned(h: Harness) {
    const agent = await h.register();
    return {agent, humanId: await h.claim(agent)};
}
const input = (humanId: string, agentId: string, overrides: Partial<ConnectCredentialInput> = {}): ConnectCredentialInput => ({humanId, authAt: Date.now(), agentId,
    provider: 'youtube', code: '4/0Aexample-code', verifier: 'v'.repeat(43), redirectUri: 'https://auth.weave.example/connect/youtube/callback', ...overrides});
const row = (h: Harness, accountId: string) => h.db.prepare(`SELECT provider,revision,target_account,target_display,secret,CAST(revoked_at AS TEXT) revoked_at
    FROM external_credentials WHERE account_id=? AND credential_ref='youtube'`).bind(accountId).first<{provider: string; revision: string; target_account: string;
    target_display: string; secret: ArrayBuffer | number[] | null; revoked_at: string | null}>();
const bytes = (value: ArrayBuffer | number[]) => value instanceof ArrayBuffer ? new Uint8Array(value) : Uint8Array.from(value);

test('connectExternalCredential_Youtube_StoresVerifiedChannelAndSealedRefreshToken', async () => {
    const h = await start();
    try {
        const {agent, humanId} = await owned(h);
        await withGoogle({}, async requests => {
            const result = await connectExternalCredential(environment(h), input(humanId, agent.accountId));
            assert.deepEqual(result, {ok: true, credentialRef: 'youtube', targetDisplay: 'HSC Beats'});
            const exchange = new URLSearchParams(requests[0].body);
            assert.equal(exchange.get('code_verifier'), 'v'.repeat(43));
            assert.equal(exchange.get('grant_type'), 'authorization_code');
            assert.equal(requests[1].auth, 'Bearer ya29.access');
        });
        const stored = (await row(h, agent.accountId))!;
        assert.equal(stored.target_account, channelId);
        assert.ok(stored.secret);
        const sealed = bytes(stored.secret);
        assert.ok(!Buffer.from(sealed).toString('latin1').includes('1//refresh'), 'the refresh token is never stored in clear');
        const binding = {accountId: agent.accountId, credentialRef: 'youtube', provider: 'youtube', revision: stored.revision};
        assert.match(await openSecret(key, binding, sealed), /^1\/\/refresh-/);
        await assert.rejects(openSecret(key, {...binding, revision: 'another'}, sealed), 'a sealed secret does not open under another revision');
        await assert.rejects(openSecret(key, {...binding, accountId: crypto.randomUUID()}, sealed), 'nor for another agent');
        const overview = await ownerOverview(environment(h), humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
        assert.deepEqual(overview.agents[0].credentials.map(c => [c.credentialRef, c.provider, c.targetAccount, c.targetDisplay, c.revision]),
            [['youtube', 'youtube', channelId, 'HSC Beats', stored.revision]]);
    } finally { await h.close(); }
});

test('connectExternalCredential_YoutubeTitleWithBidiOverride_StoredDisplayVisible', async () => {
    const h = await start();
    try {
        const {agent, humanId} = await owned(h);
        await withGoogle({channels: () => Response.json({items: [{id: channelId, snippet: {title: '\u202eHSC\tBeats\u200b'}}]})}, async () => {
            assert.deepEqual(await connectExternalCredential(environment(h), input(humanId, agent.accountId)), {ok: true, credentialRef: 'youtube', targetDisplay: 'HSC Beats'});
        });
        assert.equal((await row(h, agent.accountId))!.target_display, 'HSC Beats');
    } finally { await h.close(); }
});
test('connectExternalCredential_Reconnect_NewRevisionInvalidatesEarlierApproval', async () => {
    const h = await start();
    try {
        const {agent, humanId} = await owned(h);
        await withGoogle({}, async () => { assert.equal((await connectExternalCredential(environment(h), input(humanId, agent.accountId))).ok, true); });
        const first = (await row(h, agent.accountId))!.revision;
        await withGoogle({}, async () => { assert.equal((await connectExternalCredential(environment(h), input(humanId, agent.accountId))).ok, true); });
        const second = (await row(h, agent.accountId))!.revision;
        assert.notEqual(second, first);
    } finally { await h.close(); }
});

test('connectExternalCredential_Refusals_NothingStored', async () => {
    const h = await start();
    try {
        const {agent, humanId} = await owned(h);
        const other = await owned(h);
        const cases: [string, Partial<Google>, ConnectCredentialInput, Partial<Env>, string][] = [
            ['stale', {}, input(humanId, agent.accountId, {authAt: Date.now() - 11 * 60_000}), {}, 'stale-authentication'],
            ['another human', {}, input(other.humanId, agent.accountId), {}, 'not-found'],
            ['unconfigured', {}, input(humanId, agent.accountId), {GOOGLE_CLIENT_SECRET: undefined}, 'provider-unavailable'],
            ['exchange refused', {token: () => Response.json({error: 'invalid_grant'}, {status: 400})}, input(humanId, agent.accountId), {}, 'provider-refused'],
            ['no refresh token', {token: () => Response.json({access_token: 'a', scope})}, input(humanId, agent.accountId), {}, 'provider-refused'],
            ['scope unticked', {token: () => Response.json({access_token: 'a', refresh_token: 'r', scope: scope.split(' ')[1]})}, input(humanId, agent.accountId), {}, 'missing-scope'],
            ['no channel', {channels: () => Response.json({items: []})}, input(humanId, agent.accountId), {}, 'no-channel'],
            ['malformed channel', {channels: () => Response.json({items: [{id: 'not-a-channel', snippet: {title: 'x'}}]})}, input(humanId, agent.accountId), {}, 'provider-refused'],
            ['redirect', {token: () => new Response(null, {status: 302, headers: {Location: 'https://evil.example/'}})}, input(humanId, agent.accountId), {}, 'provider-refused'],
            ['malformed input', {}, {...input(humanId, agent.accountId), redirectUri: 'http://insecure.example/cb'}, {}, 'not-found'],
        ];
        for (const [name, google, call, env, reason] of cases)
            await withGoogle(google, async () => assert.deepEqual(await connectExternalCredential(environment(h, env), call), {ok: false, reason}, name));
        await h.db.prepare('UPDATE humans SET frozen_at=CAST(? AS INTEGER) WHERE human_id=?').bind((BigInt(Date.now()) * 10000n).toString(), humanId).run();
        await withGoogle({}, async requests => {
            assert.deepEqual(await connectExternalCredential(environment(h), input(humanId, agent.accountId)), {ok: false, reason: 'frozen'});
            assert.equal(requests.length, 0, 'the code is not exchanged for an agent the human cannot act for');
        });
        assert.equal(await row(h, agent.accountId), null);
    } finally { await h.close(); }
});

test('connectExternalCredential_OwnerFrozenDuringVerify_FrozenAndNothingStored', async () => {
    const h = await start();
    try {
        const {agent, humanId} = await owned(h);
        const setFrozen = (frozen: boolean) => h.db.prepare('UPDATE humans SET frozen_at=CAST(? AS INTEGER) WHERE human_id=?')
            .bind(frozen ? (BigInt(Date.now()) * 10000n).toString() : null, humanId).run();
        // Frozen while Google confirms the channel: the write re-checks the owner and stores nothing.
        const channels = async () => {
            await setFrozen(true);
            return Response.json({items: [{id: channelId, snippet: {title: 'HSC Beats'}}]});
        };
        await withGoogle({channels}, async requests => {
            assert.deepEqual(await connectExternalCredential(environment(h), input(humanId, agent.accountId)), {ok: false, reason: 'frozen'});
            assert.equal(requests.length, 2, 'the code was exchanged and the channel read before the owner was frozen');
        });
        assert.equal(await row(h, agent.accountId), null);
        // Positive control: the same connect succeeds once the owner is unfrozen.
        await setFrozen(false);
        await withGoogle({}, async () => assert.equal((await connectExternalCredential(environment(h), input(humanId, agent.accountId))).ok, true));
        assert.notEqual(await row(h, agent.accountId), null);
    } finally { await h.close(); }
});

test('revokeCredential_Owner_ErasesSecretAndHidesIt', async () => {
    const h = await start();
    try {
        const {agent, humanId} = await owned(h);
        const other = await owned(h);
        await withGoogle({}, async () => { await connectExternalCredential(environment(h), input(humanId, agent.accountId)); });
        const revoke = (who: string) => ownerAction(environment(h), {humanId: who, authAt: 0, kind: 'revoke-credential', agentId: agent.accountId, credentialRef: 'youtube'} as OwnerAction);
        assert.deepEqual(await revoke(other.humanId), {ok: false, reason: 'not-found'});
        assert.deepEqual(await revoke(humanId), {ok: true}, 'disconnecting needs no fresh sign-in');
        const stored = (await row(h, agent.accountId))!;
        assert.equal(stored.secret, null);
        assert.notEqual(stored.revoked_at, null);
        assert.deepEqual(await revoke(humanId), {ok: false, reason: 'not-found'});
        const overview = await ownerOverview(environment(h), humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
        assert.deepEqual(overview.agents[0].credentials, []);
    } finally { await h.close(); }
});
