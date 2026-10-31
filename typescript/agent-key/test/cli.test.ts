import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { createServer, type IncomingMessage, type Server } from 'node:http';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const cli = fileURLToPath(new URL('../src/cli.ts', import.meta.url));
const accountId = '0f8fad5b-d9cb-469f-a165-70867728950e';
const issuedToken = 'T'.repeat(43);
const limitedAccount = '7c9e6679-7425-40de-944b-e07fc1f90ae7';

type Run = {code: number; stdout: string; stderr: string};
function run(...args: string[]): Promise<Run> {
    return new Promise(resolve => execFile(process.execPath, [cli, ...args], {timeout: 30000}, (error, stdout, stderr) =>
        resolve({code: error === null ? 0 : typeof error.code === 'number' ? error.code : -1, stdout, stderr})));
}

function headerOf(request: IncomingMessage): Record<string, unknown> {
    return JSON.parse(Buffer.from(String(request.headers.dpop).split('.')[0] ?? '', 'base64url').toString());
}

/** A minimal stand-in for the relay: enough shape for the CLI, no verification (the mailbox integration test verifies). */
async function fakeRelay(): Promise<{server: Server; origin: string}> {
    const server = createServer(async (request, response) => {
        const reply = (status: number, value: unknown, headers: Record<string, string> = {}) => {
            response.writeHead(status, {'Content-Type': 'application/json', ...headers}); response.end(JSON.stringify(value));
        };
        let body = '';
        for await (const chunk of request) body += chunk;
        if (request.url === '/v1/accounts' && request.method === 'POST') {
            const jwk = headerOf(request).jwk as {crv: string; kty: string; x: string};
            const kid = createHash('sha256').update(JSON.stringify({crv: jwk.crv, kty: jwk.kty, x: jwk.x})).digest('base64url');
            return reply(200, {accountId, displayName: null, description: null, kid});
        }
        if (request.url === '/v1/token' && new URLSearchParams(body).get('client_id') === limitedAccount)
            return reply(429, {error: 'temporarily_unavailable'}, {'Retry-After': '3600'});
        if (request.url === '/v1/token' && request.method === 'POST') return reply(200, {access_token: issuedToken, token_type: 'DPoP', expires_in: 900});
        if (request.url === '/v1/keys' && request.method === 'GET' && request.headers.authorization === 'DPoP ' + issuedToken)
            return reply(200, {items: [{kid: 'k', label: null, createdAt: '2026-11-01T00:00:00Z', lastUsedAt: null, coolingUntil: null}]});
        reply(401, {status: 401, code: 'unauthenticated'});
    });
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const address = server.address();
    assert.ok(address !== null && typeof address === 'object');
    return {server, origin: `http://127.0.0.1:${address.port}`};
}

test('cli_Token_DoesNotPrintTokenWithoutFlag', async () => {
    const dir = await mkdtemp(join(tmpdir(), 'weave-agent-key-cli-'));
    const relay = await fakeRelay();
    try {
        const keyfile = join(dir, 'agent.jwk');
        assert.equal((await run('init', keyfile)).code, 0);
        const plain = await run('token', relay.origin, keyfile, accountId);
        assert.equal(plain.code, 0, plain.stderr);
        assert.ok(!(plain.stdout + plain.stderr).includes(issuedToken));
        assert.deepEqual(Object.keys(JSON.parse(plain.stdout)), ['expiresAt']);
        const flagged = await run('token', relay.origin, keyfile, accountId, '--print-token');
        assert.equal(flagged.code, 0, flagged.stderr);
        assert.equal(JSON.parse(flagged.stdout).token, issuedToken);
    } finally {
        relay.server.close();
        await rm(dir, {recursive: true, force: true});
    }
});

test('cli_NeverPrintsPrivateKey', async () => {
    const dir = await mkdtemp(join(tmpdir(), 'weave-agent-key-cli-'));
    const relay = await fakeRelay();
    try {
        const keyfile = join(dir, 'agent.jwk');
        const runs = [await run('init', keyfile)];
        const {d} = JSON.parse(await readFile(keyfile, 'utf8')) as {d: string};
        runs.push(await run('init', keyfile));
        runs.push(await run('register', relay.origin, keyfile, '--name', 'helper test'));
        runs.push(await run('token', relay.origin, keyfile, accountId));
        runs.push(await run('token', relay.origin, keyfile, accountId, '--print-token'));
        runs.push(await run('whoami', relay.origin, keyfile, accountId));
        runs.push(await run('whoami', relay.origin, keyfile, accountId, '--unknown'));
        runs.push(await run('register', 'ftp://example.test', keyfile));
        assert.deepEqual(runs.map(r => r.code), [0, 1, 0, 0, 0, 0, 2, 1]);
        assert.equal(JSON.parse(runs[2]!.stdout).accountId, accountId);
        assert.equal(JSON.parse(runs[5]!.stdout).accountId, accountId);
        for (const r of runs) assert.ok(!(r.stdout + r.stderr).includes(d), r.stdout + r.stderr);
    } finally {
        relay.server.close();
        await rm(dir, {recursive: true, force: true});
    }
});

test('cli_Token_RateLimitedBeyondOneMinute_ReportsWaitWithoutWaiting', async () => {
    const dir = await mkdtemp(join(tmpdir(), 'weave-agent-key-cli-'));
    const relay = await fakeRelay();
    try {
        const keyfile = join(dir, 'agent.jwk');
        assert.equal((await run('init', keyfile)).code, 0);
        const limited = await run('token', relay.origin, keyfile, limitedAccount);
        assert.equal(limited.code, 1);
        assert.equal(limited.stdout, '');
        assert.equal(limited.stderr, 'weave-agent-key: Token request answered HTTP 429 temporarily_unavailable; retry after 3600 s\n');
    } finally {
        relay.server.close();
        await rm(dir, {recursive: true, force: true});
    }
});
