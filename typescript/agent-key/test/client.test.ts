import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { accessToken, createKey, fetchWithKey, loadKey, register, WeaveHttpError, type AgentKey, type HelperOptions } from '../src/index.ts';

const origin = 'https://relay.example';
const accountId = '0f8fad5b-d9cb-469f-a165-70867728950e';
const secret = 'xyzSECRETbodytext';

async function withKey(body: (key: AgentKey) => Promise<void>): Promise<void> {
    const dir = await mkdtemp(join(tmpdir(), 'weave-agent-key-client-'));
    try {
        await createKey(join(dir, 'agent.jwk'));
        await body(await loadKey(join(dir, 'agent.jwk')));
    } finally { await rm(dir, {recursive: true, force: true}); }
}

type Seen = {url: string; signal: AbortSignal | null | undefined};
function relay(answer: (url: string) => Response, seen: Seen[] = []): typeof fetch {
    return (async (input: string | URL | Request, init: RequestInit = {}) => {
        seen.push({url: String(input), signal: init.signal});
        return answer(String(input));
    }) as typeof fetch;
}
const json = (status: number, value: unknown, headers: Record<string, string> = {}) =>
    new Response(JSON.stringify(value), {status, headers: {'Content-Type': 'application/json', ...headers}});
const token = {token: 'T'.repeat(43), expiresAt: '2026-11-01T00:15:00.000Z', accountId};

test('client_NonJsonSuccessBody_UnexpectedResponseWithoutEchoingBody', async () => {
    await withKey(async key => {
        const options: HelperOptions = {fetch: relay(() => new Response(secret, {status: 200}))};
        for (const call of [() => register(origin, key, {}, options), () => accessToken(origin, key, accountId, options)]) {
            await assert.rejects(call(), (error: unknown) => error instanceof WeaveHttpError && error.code === 'unexpected-response'
                && !error.message.includes('xyz') && !error.message.includes('SECRET'));
        }
    });
});

test('client_RelayErrorCode_BoundedAndRestrictedToSafeCharacters', async () => {
    await withKey(async key => {
        const cases: [unknown, string | null][] = [
            [{error: 'invalid_client'}, 'invalid_client'],
            [{code: 'rate-limited.v1 x'}, 'rate-limited.v1 x'],
            [{error: '\u001b[31mred'}, 'unexpected-error-code'],
            [{error: 'a'.repeat(65)}, 'unexpected-error-code'],
            [{error: ''}, 'unexpected-error-code'],
            [{code: 'line\nbreak'}, 'unexpected-error-code'],
            [{other: 'x'}, null],
        ];
        for (const [body, expected] of cases) {
            const options: HelperOptions = {fetch: relay(() => json(400, body))};
            await assert.rejects(register(origin, key, {}, options), (error: unknown) => error instanceof WeaveHttpError && error.code === expected
                && !/[\u0000-\u001f]/.test(error.message) && error.message.length < 120, JSON.stringify(body));
        }
    });
});

test('client_NoSignal_EachRequestHasDefaultTimeoutSignal', async () => {
    await withKey(async key => {
        const seen: Seen[] = [];
        const options: HelperOptions = {fetch: relay(url => url.endsWith('/v1/token')
            ? json(200, {access_token: 'A'.repeat(43), token_type: 'DPoP', expires_in: 900}) : json(200, {items: []}), seen)};
        await accessToken(origin, key, accountId, options);
        await fetchWithKey(origin, key, token, 'GET', '/v1/keys', undefined, options);
        assert.equal(seen.length, 2);
        for (const {signal} of seen) assert.ok(signal instanceof AbortSignal && !signal.aborted);
    });
});

test('client_CallerSignal_PassedToFetchAndCancelsRetryAfterWait', async () => {
    await withKey(async key => {
        const controller = new AbortController();
        const seen: Seen[] = [];
        const options: HelperOptions = {signal: controller.signal, fetch: relay(() => json(429, {error: 'slow_down'}, {'Retry-After': '30'}), seen)};
        const started = Date.now();
        setTimeout(() => controller.abort(new Error('caller stopped')), 50);
        await assert.rejects(accessToken(origin, key, accountId, options), {message: 'caller stopped'});
        assert.ok(Date.now() - started < 5000, 'the 30 s wait was cancelled');
        assert.equal(seen.length, 1);
        assert.equal(seen[0]?.signal, controller.signal);
    });
});
