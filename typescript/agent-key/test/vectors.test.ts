// Cross-language wire vectors produced by the Python helper (python/weave-agent-key/tests/test_vectors.py) from a TEST-ONLY key.
// Ed25519 is deterministic, so this helper must produce the same thumbprint, signing inputs and signatures, and the mailbox
// Worker's own DPoP and JWS code must accept the Python-produced proofs.
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { accessToken, fetchWithKey, loadKey } from '../src/index.ts';

// The mailbox Worker's own modules, loaded at run time: they are type-checked by the Worker package under its own settings.
type Ed25519Jwk = {kty: 'OKP'; crv: 'Ed25519'; x: string};
type CompactJws = {header: Record<string, unknown>; payload: Record<string, unknown>};
type MailboxDpop = {verifyDpop(request: Request, options: {now: number; accessToken?: string}): Promise<{ok: true; jkt: string} | {ok: false}>};
type MailboxJose = {
    jwkThumbprint(jwk: Ed25519Jwk): Promise<string>; parseJws(token: string): CompactJws | null; publicJwk(value: unknown): Ed25519Jwk | null;
    verifyJws(jws: CompactJws, jwk: Ed25519Jwk): Promise<boolean>;
};
const mailboxSource = new URL('../../mailbox-worker/src/', import.meta.url);
const {verifyDpop} = await import(new URL('dpop.ts', mailboxSource).href) as MailboxDpop;
const {jwkThumbprint, parseJws, publicJwk, verifyJws} = await import(new URL('jose.ts', mailboxSource).href) as MailboxJose;

type Jws = {signingInput: string; signature: string};
type Vectors = {
    testOnlyKey: {kty: 'OKP'; crv: 'Ed25519'; x: string; d: string}; kid: string; origin: string; now: number; jti: string; accountId: string;
    accessToken: string; tokenRequest: {url: string; assertion: Jws; dpop: Jws}; keyList: {path: string; url: string; dpop: Jws};
};

const vectors = JSON.parse(await readFile(new URL('../../../python/weave-agent-key/tests/vectors/agent-key-vectors.json', import.meta.url), 'utf8')) as Vectors;
const compact = (jws: Jws) => `${jws.signingInput}.${jws.signature}`;

test('vectors_TypeScriptHelper_MatchesPythonVectors', async t => {
    const dir = await mkdtemp(join(tmpdir(), 'weave-agent-key-vectors-'));
    try {
        const path = join(dir, 'test-only.jwk');
        await writeFile(path, JSON.stringify(vectors.testOnlyKey), {mode: 0o600});
        const key = await loadKey(path);
        assert.equal(key.kid, vectors.kid);
        t.mock.method(crypto, 'randomUUID', () => vectors.jti);
        const sent: {url: string; headers: Headers; body: string | null}[] = [];
        const options = {
            now: () => vectors.now * 1000 + 750,
            fetch: (async (input: string | URL | Request, init: RequestInit = {}) => {
                const url = String(input);
                sent.push({url, headers: new Headers(init.headers), body: typeof init.body === 'string' ? init.body : null});
                const body = url.endsWith('/v1/token') ? {access_token: vectors.accessToken, token_type: 'DPoP', expires_in: 900} : {items: []};
                return new Response(JSON.stringify(body), {status: 200, headers: {'Content-Type': 'application/json'}});
            }) as typeof fetch,
        };
        const issued = await accessToken(vectors.origin, key, vectors.accountId, options);
        await fetchWithKey(vectors.origin, key, issued, 'get', vectors.keyList.path, undefined, options);
        const [tokenRequest, keyList] = sent;
        assert.equal(tokenRequest?.url, vectors.tokenRequest.url);
        assert.equal(new URLSearchParams(tokenRequest.body ?? '').get('client_assertion'), compact(vectors.tokenRequest.assertion));
        assert.equal(tokenRequest.headers.get('DPoP'), compact(vectors.tokenRequest.dpop));
        assert.equal(keyList?.url, vectors.keyList.url);
        assert.equal(keyList.headers.get('Authorization'), `DPoP ${vectors.accessToken}`);
        assert.equal(keyList.headers.get('DPoP'), compact(vectors.keyList.dpop));
    } finally { await rm(dir, {recursive: true, force: true}); }
});

test('vectors_MailboxVerifiesPythonProofs', async () => {
    const jwk = publicJwk({kty: vectors.testOnlyKey.kty, crv: vectors.testOnlyKey.crv, x: vectors.testOnlyKey.x});
    assert.ok(jwk);
    assert.equal(await jwkThumbprint(jwk), vectors.kid);
    const now = vectors.now;
    const tokenProof = await verifyDpop(new Request(vectors.tokenRequest.url, {method: 'POST', headers: {DPoP: compact(vectors.tokenRequest.dpop)}}), {now});
    assert.deepEqual(tokenProof.ok && tokenProof.jkt, vectors.kid);
    const keysProof = await verifyDpop(new Request(vectors.keyList.url, {headers: {DPoP: compact(vectors.keyList.dpop)}}), {now, accessToken: vectors.accessToken});
    assert.deepEqual(keysProof.ok && keysProof.jkt, vectors.kid);
    const assertion = parseJws(compact(vectors.tokenRequest.assertion));
    assert.ok(assertion);
    assert.deepEqual(assertion.header, {alg: 'EdDSA', kid: vectors.kid});
    assert.deepEqual(assertion.payload, {iss: vectors.accountId, sub: vectors.accountId, aud: vectors.tokenRequest.url, iat: now, exp: now + 60, jti: vectors.jti});
    assert.equal(await verifyJws(assertion, jwk), true);
    // Negative controls: the same proofs are refused for another token, method or a tampered signature.
    assert.equal((await verifyDpop(new Request(vectors.keyList.url, {headers: {DPoP: compact(vectors.keyList.dpop)}}), {now, accessToken: 'other'})).ok, false);
    assert.equal((await verifyDpop(new Request(vectors.keyList.url, {method: 'POST', headers: {DPoP: compact(vectors.keyList.dpop)}}),
        {now, accessToken: vectors.accessToken})).ok, false);
    const flipped = vectors.tokenRequest.assertion.signature.startsWith('A') ? 'B' : 'A';
    const tampered = parseJws(`${vectors.tokenRequest.assertion.signingInput}.${flipped}${vectors.tokenRequest.assertion.signature.slice(1)}`);
    assert.equal(tampered !== null && await verifyJws(tampered, jwk), false);
});
