// Drives the dependency-free TypeScript agent key helper (typescript/agent-key) against the real Worker in Miniflare.
import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { start } from './harness.ts';
import { accessToken, createKey, dpopProof, fetchWithKey, loadKey, RateLimitedError, register, WeaveHttpError, type AgentKey, type HelperOptions } from '../../agent-key/src/index.ts';
import { accessTokenHash, verifyDpop } from '../src/dpop.ts';
import { agentLimits } from '../src/agent-limits.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Call = {method: string; path: string; status: number; body: string | null};

const origin = 'http://localhost';

async function withKey(run: (key: AgentKey) => Promise<void>): Promise<void> {
    const dir = await mkdtemp(join(tmpdir(), 'weave-agent-key-helper-'));
    try {
        const path = join(dir, 'agent.jwk');
        await createKey(path);
        await run(await loadKey(path));
    } finally { await rm(dir, {recursive: true, force: true}); }
}

/** Sends to the Worker through Miniflare and records each request; `override` can answer instead of the Worker. */
function relay(h: Harness, calls: Call[], override?: (url: URL, init: RequestInit) => Response | undefined): HelperOptions['fetch'] {
    return (async (input: string | URL | Request, init: RequestInit = {}) => {
        const url = new URL(String(input));
        const response = override?.(url, init) ?? await h.mf.dispatchFetch(url.href, init as never) as unknown as Response;
        calls.push({method: init.method ?? 'GET', path: url.pathname, status: response.status, body: typeof init.body === 'string' ? init.body : null});
        return response;
    }) as typeof fetch;
}

const payloadOf = (jws: string) => JSON.parse(Buffer.from(jws.split('.')[1] ?? '', 'base64url').toString()) as Record<string, unknown>;
const unauthenticated = () => new Response(JSON.stringify({status: 401, code: 'unauthenticated'}), {status: 401, headers: {'Content-Type': 'application/problem+json'}});

test('dpop_ClaimsExact', async () => withKey(async key => {
    const token = 'A'.repeat(43), now = Math.floor(Date.now() / 1000);
    const url = new URL(origin + '/v1/inbox?limit=10#top');
    const proof = await dpopProof(key, 'get', url, token);
    const claims = payloadOf(proof);
    assert.deepEqual(Object.keys(claims).sort(), ['ath', 'htm', 'htu', 'iat', 'jti']);
    assert.equal(claims.htu, origin + '/v1/inbox');
    assert.equal(claims.htm, 'GET');
    assert.equal(claims.ath, await accessTokenHash(token));
    assert.ok(Math.abs((claims.iat as number) - now) <= 1);
    const header = JSON.parse(Buffer.from(proof.split('.')[0]!, 'base64url').toString());
    assert.deepEqual(header, {alg: 'EdDSA', typ: 'dpop+jwt', jwk: key.publicJwk});
    const again = payloadOf(await dpopProof(key, 'get', url, token));
    assert.equal(typeof claims.jti, 'string');
    assert.notEqual(again.jti, claims.jti, 'every proof carries a new jti');
    const verified = await verifyDpop(new Request(url.href, {headers: {DPoP: proof}}), {now, accessToken: token});
    assert.deepEqual(verified.ok && verified.jkt, key.kid);
    // Negative controls: the mailbox refuses the same proof for another method or token.
    assert.equal((await verifyDpop(new Request(url.href, {method: 'POST', headers: {DPoP: proof}}), {now, accessToken: token})).ok, false);
    assert.equal((await verifyDpop(new Request(url.href, {headers: {DPoP: proof}}), {now, accessToken: 'B'.repeat(43)})).ok, false);
    const unbound = await dpopProof(key, 'POST', new URL(origin + '/v1/token'));
    assert.equal('ath' in payloadOf(unbound), false);
    assert.equal((await verifyDpop(new Request(origin + '/v1/token', {method: 'POST', headers: {DPoP: unbound}}), {now})).ok, true);
}));

test('helper_RegisterTokenWhoami_RoundTrip', async () => {
    const h = await start();
    try {
        await withKey(async key => {
            const calls: Call[] = [], options = {fetch: relay(h, calls)};
            const {accountId} = await register(origin, key, {displayName: 'Helper agent'}, options);
            assert.deepEqual(await register(origin, key, {}, options), {accountId}, 'a live key re-registers as the same agent');
            const issued = await accessToken(origin, key, accountId, options);
            assert.match(issued.token, /^[A-Za-z0-9_-]{43}$/);
            assert.ok(Date.parse(issued.expiresAt) > Date.now() + 14 * 60_000);
            const tokenCall = calls.find(c => c.path === '/v1/token')!;
            const assertion = payloadOf(new URLSearchParams(tokenCall.body!).get('client_assertion')!);
            assert.equal(assertion.aud, origin + '/v1/token');
            assert.equal(assertion.iss, accountId);
            assert.equal(assertion.sub, accountId);
            assert.ok((assertion.exp as number) - (assertion.iat as number) <= 60);
            const {response, token} = await fetchWithKey(origin, key, issued, 'GET', '/v1/keys', undefined, options);
            assert.equal(response.status, 200);
            assert.equal(token, issued);
            const keys = await response.json() as {items: {kid: string}[]};
            assert.deepEqual(keys.items.map(item => item.kid), [key.kid]);
            const created = await fetchWithKey(origin, key, issued, 'post', '/v1/addresses', {}, options);
            assert.equal(created.response.status, 200, await created.response.clone().text());
            assert.deepEqual(calls.map(c => `${c.method} ${c.path} ${c.status}`),
                ['POST /v1/accounts 200', 'POST /v1/accounts 200', 'POST /v1/token 200', 'GET /v1/keys 200', 'POST /v1/addresses 200']);
        });
    } finally { await h.close(); }
});

test('helper_401FromToken_RenewsOnce', async () => {
    const h = await start();
    try {
        await withKey(async key => {
            const setup: Call[] = [];
            const {accountId} = await register(origin, key, {}, {fetch: relay(h, setup)});
            const issued = await accessToken(origin, key, accountId, {fetch: relay(h, setup)});
            // An unknown token gets a real 401; the helper obtains one new token and repeats the request.
            const calls: Call[] = [];
            const stale = {...issued, token: 'A'.repeat(43)};
            const renewed = await fetchWithKey(origin, key, stale, 'GET', '/v1/keys', undefined, {fetch: relay(h, calls)});
            assert.equal(renewed.response.status, 200);
            assert.notEqual(renewed.token.token, stale.token);
            assert.equal(renewed.token.accountId, accountId);
            assert.deepEqual(calls.map(c => `${c.method} ${c.path} ${c.status}`), ['GET /v1/keys 401', 'POST /v1/token 200', 'GET /v1/keys 200']);
            // A second 401 after renewal is returned, not renewed again.
            const repeated: Call[] = [];
            const refused = await fetchWithKey(origin, key, issued, 'GET', '/v1/keys', undefined,
                {fetch: relay(h, repeated, url => url.pathname === '/v1/keys' ? unauthenticated() : undefined)});
            assert.equal(refused.response.status, 401);
            assert.deepEqual(repeated.map(c => `${c.method} ${c.path} ${c.status}`), ['GET /v1/keys 401', 'POST /v1/token 200', 'GET /v1/keys 401']);
        });
    } finally { await h.close(); }
});

test('helper_401FromRegister_NotRetried', async () => {
    const h = await start();
    try {
        await withKey(async key => {
            // A proof ten minutes old is outside the relay's ±60 s window: the Worker itself answers 401.
            const calls: Call[] = [];
            const skewed = {fetch: relay(h, calls), now: () => Date.now() - 600_000};
            await assert.rejects(register(origin, key, {}, skewed), (error: unknown) => error instanceof WeaveHttpError && error.status === 401 && error.code === 'unauthenticated');
            assert.deepEqual(calls.map(c => `${c.method} ${c.path} ${c.status}`), ['POST /v1/accounts 401']);
            // Requests authenticated by the key alone are never sent through the token-renewing path.
            const token = {token: 'A'.repeat(43), expiresAt: new Date().toISOString(), accountId: crypto.randomUUID()};
            for (const path of ['/v1/accounts', '/v1/key-requests', '/v1/token'])
                await assert.rejects(fetchWithKey(origin, key, token, 'POST', path, {}, {fetch: relay(h, calls)}), TypeError);
            assert.equal(calls.length, 1);
        });
    } finally { await h.close(); }
});

test('helper_429_HonoursRetryAfterOnce', async () => {
    const h = await start();
    try {
        await withKey(async key => {
            const setup: Call[] = [];
            const {accountId} = await register(origin, key, {}, {fetch: relay(h, setup)});
            const limitedOnce = (calls: Call[], retryAfter: string) => {
                let limited = false;
                return relay(h, calls, url => {
                    if (url.pathname !== '/v1/token' || limited) return undefined;
                    limited = true;
                    return new Response(JSON.stringify({error: 'temporarily_unavailable'}), {status: 429, headers: {'Retry-After': retryAfter}});
                });
            };
            // A 429 with Retry-After of at most 60 s is waited out once, then the request succeeds with a new assertion.
            const waits: number[] = [], calls: Call[] = [];
            await accessToken(origin, key, accountId, {fetch: limitedOnce(calls, '60'), sleep: async ms => { waits.push(ms); }});
            assert.deepEqual(waits, [60_000]);
            assert.deepEqual(calls.map(c => c.status), [429, 200]);
            const [first, second] = calls.map(c => new URLSearchParams(c.body!).get('client_assertion')!);
            assert.notEqual(payloadOf(first!).jti, payloadOf(second!).jti);
            // A longer Retry-After is not waited: the typed error carries it for the caller.
            const longCalls: Call[] = [], longWaits: number[] = [];
            await assert.rejects(accessToken(origin, key, accountId, {fetch: limitedOnce(longCalls, '61'), sleep: async ms => { longWaits.push(ms); }}),
                (error: unknown) => error instanceof RateLimitedError && error.status === 429 && error.retryAfter === 61 && /retry after 61 s/.test(error.message));
            assert.deepEqual(longCalls.map(c => c.status), [429]);
            assert.deepEqual(longWaits, []);
            // The Worker's own hourly limit (Retry-After is the time to the next UTC hour): at most one wait, then the typed error.
            for (let issued = 1; issued < agentLimits.unclaimed.tokensPerHour; issued++) await accessToken(origin, key, accountId, {fetch: relay(h, setup)});
            const exhausted: Call[] = [], limitWaits: number[] = [];
            const error = await accessToken(origin, key, accountId, {fetch: relay(h, exhausted), sleep: async ms => { limitWaits.push(ms); }})
                .then(() => assert.fail('the hourly limit was not reported'), (caught: unknown) => caught);
            assert.ok(error instanceof RateLimitedError && error.code === 'temporarily_unavailable', String(error));
            assert.ok(error.retryAfter > 0 && error.retryAfter <= 3600);
            const waited = limitWaits.length === 1;
            assert.deepEqual(exhausted.map(c => c.status), waited ? [429, 429] : [429]);
            assert.ok(waited ? limitWaits[0]! <= 60_000 : error.retryAfter > 60);
        });
    } finally { await h.close(); }
});

test('helper_MalformedToken_RefusedWithoutEcho', async () => {
    const h = await start();
    try {
        await withKey(async key => {
            const {accountId} = await register(origin, key, {}, {fetch: relay(h, [])});
            for (const bad of ['line\r\nInjected: header', 'has space', 'é'.repeat(10), 'A'.repeat(4097), '=leading']) {
                const issuing = relay(h, [], url => url.pathname === '/v1/token'
                    ? new Response(JSON.stringify({access_token: bad, token_type: 'DPoP', expires_in: 900}), {status: 200, headers: {'Content-Type': 'application/json'}}) : undefined);
                await assert.rejects(accessToken(origin, key, accountId, {fetch: issuing}),
                    (error: unknown) => error instanceof WeaveHttpError && error.code === 'unexpected-response' && !error.message.includes(bad), JSON.stringify(bad.slice(0, 20)));
                const calls: Call[] = [];
                await assert.rejects(fetchWithKey(origin, key, {token: bad, expiresAt: new Date().toISOString(), accountId}, 'GET', '/v1/keys', undefined, {fetch: relay(h, calls)}),
                    (error: unknown) => error instanceof TypeError && !error.message.includes(bad));
                assert.equal(calls.length, 0);
            }
        });
    } finally { await h.close(); }
});
