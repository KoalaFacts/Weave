import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { build } from 'esbuild';
import { start, testGrant } from './harness.ts';
import { inProcessMedia } from './in-process-media.ts';
import type { Env } from '../src/database.ts';
// Evaluate the bundled production module, as the other private-tool tests do.
const built = await build({entryPoints: ['src/remote-private.ts'], bundle: true, write: false, platform: 'node', format: 'esm', logLevel: 'silent'});
const {executePrivateMcp} = await import('data:text/javascript;base64,' + Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');

type Harness = Awaited<ReturnType<typeof start>>;
const key = randomBytes(32).toString('base64url'), mediaOrigin = 'https://media.weave.test';
const file = randomBytes(8192), sha = createHash('sha256').update(file).digest('hex');
/** What the fake web serves per path; every request the Worker makes is recorded. */
const sources: Record<string, () => Response> = {
    '/clip.mp4': () => new Response(file, {headers: {'Content-Type': 'video/mp4; codecs="avc1"', 'Content-Length': String(file.length)}}),
    '/moved.mp4': () => new Response(null, {status: 302, headers: {Location: 'https://cdn.example.com/clip.mp4'}}),
    '/missing.mp4': () => new Response('gone', {status: 404}),
    '/page.html': () => new Response('<html></html>', {headers: {'Content-Type': 'text/html'}}),
    '/unsized.mp4': () => new Response(new ReadableStream({start(c) { c.enqueue(file); c.close(); }}), {headers: {'Content-Type': 'video/mp4'}}),
};
async function scenario(run: (s: {h: Harness; seen: string[]}) => Promise<void>) {
    const seen: string[] = [];
    const h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key, EXTERNAL_MEDIA_ORIGIN: mediaOrigin}, outboundService: async (request: Request) => {
        seen.push(request.method + ' ' + request.url);
        const url = new URL(request.url);
        return url.hostname === 'cdn.example.com' && sources[url.pathname] ? sources[url.pathname]() : new Response('refused', {status: 599});
    }});
    try { await run({h, seen}); } finally { await h.close(); }
}
const objects = async (h: Harness) => (await (await h.mf.getR2Bucket('EXTERNAL_MEDIA')).list()).objects.length;
const importing = (h: Harness, agent: any, value: unknown) => h.request('/v1/external-media/imports', agent, 'POST', value);

test('importMedia_PublicFile_StoredAsTheExactFetchedFile', () => scenario(async ({h, seen}) => {
    const agent = await h.register();
    const response = await importing(h, agent, {url: 'https://cdn.example.com/clip.mp4'});
    assert.equal(response.status, 200, await response.clone().text());
    const record = await response.json() as Record<string, any>;
    assert.deepEqual([record.contentType, record.bytes, record.sha256], ['video/mp4', file.length, sha]);
    assert.deepEqual(seen, ['GET https://cdn.example.com/clip.mp4']);
    const stored = await (await h.mf.getR2Bucket('EXTERNAL_MEDIA')).get(agent.accountId + '/' + record.mediaId);
    assert.deepEqual(Buffer.from(await stored!.arrayBuffer()), file);
    assert.deepEqual(await h.ok(`/v1/external-media/${record.mediaId}`, agent), record);
}));

test('importMedia_UnsafeOrMalformedUrl_RefusedWithoutAnyRequest', () => scenario(async ({h, seen}) => {
    const agent = await h.register();
    for (const url of ['http://cdn.example.com/clip.mp4', 'https://127.0.0.1/clip.mp4', 'https://2130706433/clip.mp4', 'https://0x7f.1/clip.mp4',
        'https://[::1]/clip.mp4', 'https://localhost/clip.mp4', 'https://localhost./clip.mp4', 'https://printer.local./clip.mp4',
        'https://media.weave.test./v1/external-media/x/content', 'https://printer.local/clip.mp4', 'https://db.internal/clip.mp4',
        'https://cdn.example.com:8443/clip.mp4', 'https://user:pw@cdn.example.com/clip.mp4', 'https://media.weave.test/v1/external-media/x/content',
        'https://cdn.example.com/' + 'a'.repeat(2048), 'not a url', 42]) {
        const response = await importing(h, agent, {url});
        assert.equal(response.status, 400, String(url));
        assert.equal(((await response.json()) as any).field, 'url', String(url));
    }
    assert.equal((await importing(h, agent, {url: 'https://cdn.example.com/clip.mp4', contentType: 'video/mp4'})).status, 400, 'no other fields');
    assert.deepEqual(seen, []);
}));

test('importMedia_SourceRefusesOrMisdescribes_NothingStored', () => scenario(async ({h}) => {
    const agent = await h.register();
    for (const [path, reason] of [['/moved.mp4', 'must not redirect'], ['/missing.mp4', 'answered 404, not 200'], ['/page.html', 'must serve'],
        ['/unsized.mp4', 'must state a Content-Length'], ['/other.mp4', 'answered 599, not 200']]) {
        const response = await importing(h, agent, {url: 'https://cdn.example.com' + path});
        assert.equal(response.status, 400, path);
        assert.ok(((await response.json()) as any).reason.startsWith(reason), path);
    }
    assert.equal(await objects(h), 0);
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_media').first<number>('n'), 0);
}));

test('importMediaFromUrl_McpGrant_ChecksAuthorityBeforeFetching', () => scenario(async ({h}) => {
    const agent = await h.register();
    await h.claim(agent);
    const grantId = await testGrant(h.db, agent.accountId);
    const env = {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '10', REGISTRATION_GLOBAL_PER_HOUR: '100',
        CLEANUP_MAX_MESSAGES: '0', CLEANUP_TIME_BUDGET_MS: '0', EXTERNAL_CREDENTIAL_KEY: key, EXTERNAL_MEDIA_ORIGIN: mediaOrigin,
        EXTERNAL_MEDIA: inProcessMedia(await h.mf.getR2Bucket('EXTERNAL_MEDIA'))} as unknown as Env;
    // The tool runs in this process, so its fetch is this process's fetch.
    const fetched: {url: string; redirect?: string}[] = [], original = globalThis.fetch;
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        fetched.push({url: String(input), redirect: init?.redirect});
        return sources['/clip.mp4']();
    };
    const call = async () => {
        const response = await executePrivateMcp(env, 'https://demo.test', agent.accountId, 'import_media_from_url', {url: 'https://cdn.example.com/clip.mp4'}, grantId);
        return {status: response.status, body: await response.json() as Record<string, any>};
    };
    try {
        const imported = await call();
        assert.equal(imported.status, 200, JSON.stringify(imported.body));
        assert.equal(imported.body.sha256, sha);
        assert.deepEqual(fetched, [{url: 'https://cdn.example.com/clip.mp4', redirect: 'manual'}]);
        await h.db.prepare('UPDATE client_grants SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=?').bind((BigInt(Date.now()) * 10000n).toString(), grantId).run();
        assert.equal((await call()).status, 401);
        assert.equal(fetched.length, 1, 'a revoked grant makes no outbound request');
        assert.equal(await objects(h), 1);
    } finally { globalThis.fetch = original; }
}));

test('importMediaFromUrl_SourceBreaksOffOrStorageFails_EachKeepsItsOwnMeaning', () => scenario(async ({h}) => {
    const agent = await h.register();
    await h.claim(agent);
    const grantId = await testGrant(h.db, agent.accountId);
    const bucket = inProcessMedia(await h.mf.getR2Bucket('EXTERNAL_MEDIA'));
    let storageDown = false;
    const env = {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '10', REGISTRATION_GLOBAL_PER_HOUR: '100',
        CLEANUP_MAX_MESSAGES: '0', CLEANUP_TIME_BUDGET_MS: '0', EXTERNAL_CREDENTIAL_KEY: key, EXTERNAL_MEDIA_ORIGIN: mediaOrigin,
        EXTERNAL_MEDIA: {...bucket, put: async (k: string, body: ReadableStream, options: unknown) => {
            if (storageDown) throw Error('R2 unavailable');
            return bucket.put(k, body, options);
        }}} as unknown as Env;
    let serve = () => new Response(file.subarray(0, 100), {headers: {'Content-Type': 'video/mp4', 'Content-Length': String(file.length)}});
    const original = globalThis.fetch;
    globalThis.fetch = async () => serve();
    const call = async () => {
        const response = await executePrivateMcp(env, 'https://demo.test', agent.accountId, 'import_media_from_url', {url: 'https://cdn.example.com/clip.mp4'}, grantId);
        return {status: response.status, body: await response.json() as Record<string, any>};
    };
    try {
        const short = await call();
        assert.deepEqual([short.status, short.body.field, short.body.reason], [400, 'url', 'could not be fetched completely'], 'a body shorter than stated is the source\'s failure');
        serve = sources['/clip.mp4'];
        storageDown = true;
        const down = await call();
        assert.equal(down.status, 503, JSON.stringify(down.body));
        assert.notEqual(down.body.field, 'url', 'a storage outage is not blamed on the URL');
        assert.equal(await objects(h), 0);
    } finally { globalThis.fetch = original; }
}));
