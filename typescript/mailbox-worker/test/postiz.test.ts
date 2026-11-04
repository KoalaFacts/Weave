import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { connectPostiz } from '../src/external-credentials.ts';
import { openSecret } from '../src/credential-vault.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const key = randomBytes(32).toString('base64url');
const apiKey = 'postiz-api-key-' + randomBytes(8).toString('hex');
const api = 'https://api.postiz.com/public/v1';
const integrations = [
    {id: 'cm4tiktok00000000000000001', name: 'HSC Beats', identifier: 'tiktok', profile: 'hscbeats', disabled: false},
    {id: 'cm4linkedin000000000000001', name: 'Koala Facts', identifier: 'linkedin-page', profile: 'koalafacts', disabled: false},
    {id: 'cm4disabled000000000000001', name: 'Old', identifier: 'x', profile: 'old', disabled: true},
    {id: 'cm4unsupported0000000000001', name: 'Shop', identifier: 'gmb', profile: 'shop', disabled: false}];
const environment = (h: Harness) => ({db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
    REGISTRATION_GLOBAL_PER_HOUR: '100', EXTERNAL_CREDENTIAL_KEY: key, EXTERNAL_MEDIA_ORIGIN: 'http://localhost'}) as unknown as Env;

type Postiz = {posts: any[]; uploads: string[]; fetched: Buffer[]; post: () => Response; upload: (bytes: Buffer) => Response};
async function scenario(run: (s: {h: Harness; p: Postiz; env: Env}) => Promise<void>) {
    const p: Postiz = {posts: [], uploads: [], fetched: [],
        post: () => Response.json([{postId: 'post-' + p.posts.length, integration: 'x'}]),
        upload: bytes => Response.json({id: 'media-' + p.fetched.length, path: 'https://uploads.postiz.com/' + createHash('sha256').update(bytes).digest('hex').slice(0, 8), status: 'ready'})};
    let h: Harness;
    h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key, EXTERNAL_MEDIA_ORIGIN: 'http://localhost'}, outboundService: async (request: Request) => {
        if (request.headers.get('authorization') !== apiKey) return Response.json({message: 'Unauthorized'}, {status: 401});
        if (request.method === 'POST' && request.url === api + '/upload-from-url') {
            const {url} = await request.json() as {url: string};
            p.uploads.push(url);
            // Postiz fetches the signed URL itself, as its backend would.
            const fetched = await h.mf.dispatchFetch(url);
            if (fetched.status !== 200) return Response.json({message: 'fetch failed'}, {status: 400});
            const bytes = Buffer.from(await fetched.arrayBuffer());
            p.fetched.push(bytes);
            return p.upload(bytes);
        }
        if (request.method === 'POST' && request.url === api + '/posts') { p.posts.push(await request.json()); return p.post(); }
        return new Response('refused', {status: 599});
    }});
    try { await run({h, p, env: environment(h)}); } finally { await h.close(); }
}

/** Replaces fetch for Postiz's channel listing while `run` executes (connectPostiz runs in this process). */
async function withIntegrations(respond: () => Response, run: () => Promise<void>) {
    const original = globalThis.fetch;
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        assert.equal(String(input), api + '/integrations');
        assert.equal(init?.redirect, 'manual');
        return new Headers(init?.headers).get('Authorization') === apiKey ? respond() : Response.json({message: 'Unauthorized'}, {status: 401});
    };
    try { await run(); } finally { globalThis.fetch = original; }
}
async function connected(h: Harness, env: Env) {
    const agent = await h.register(), humanId = await h.claim(agent);
    await withIntegrations(() => Response.json(integrations), async () => {
        assert.deepEqual(await connectPostiz(env, {humanId, authAt: Date.now(), agentId: agent.accountId, apiKey}),
            {ok: true, channels: ['tiktok · HSC Beats (@hscbeats)', 'linkedin-page · Koala Facts (@koalafacts)']});
    });
    return {agent, humanId};
}
const tiktok = {__type: 'tiktok', privacy_level: 'PUBLIC_TO_EVERYONE', duet: false, stitch: false, comment: true, autoAddMusic: false,
    brand_content_toggle: false, brand_organic_toggle: false, content_posting_method: 'DIRECT_POST'};
const propose = (h: Harness, agent: any, credentialRef: string, content: unknown) =>
    h.request('/v1/external-actions', agent, 'POST', {credentialRef, operation: 'publish_post', content: JSON.stringify(content)});
const upload = async (h: Harness, agent: any, bytes: Buffer, type: string) => (await h.fetchAs(agent, 'http://localhost/v1/external-media',
    {method: 'PUT', headers: {'Content-Type': type, 'Content-Length': String(bytes.length)}, body: bytes})).json() as Promise<{mediaId: string; bytes: number; sha256: string}>;
const pin = (m: {mediaId: string; bytes: number; sha256: string}) => ({mediaId: m.mediaId, bytes: m.bytes, sha256: m.sha256});
async function approve(h: Harness, env: Env, humanId: string, actionId: string) {
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    const shown = overview.externalActions.find(a => a.actionId === actionId)!;
    assert.deepEqual(await ownerAction(env, {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: actionId, planDigest: shown.planDigest} as OwnerAction), {ok: true});
}
const execute = (h: Harness, agent: any, actionId: string) => h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {});
async function approvedPost(h: Harness, env: Env, content: unknown, credentialRef = 'postiz:' + integrations[1].id) {
    const {agent, humanId} = await connected(h, env);
    const response = await propose(h, agent, credentialRef, typeof content === 'function' ? await (content as (a: any) => Promise<unknown>)(agent) : content);
    assert.equal(response.status, 200, await response.clone().text());
    const {actionId} = await response.json() as {actionId: string};
    await approve(h, env, humanId, actionId);
    return {agent, humanId, actionId};
}

test('connectPostiz_ApiKey_OneSealedCredentialPerEnabledSupportedChannel', () => scenario(async ({h, env}) => {
    const {agent, humanId} = await connected(h, env);
    const rows = (await h.db.prepare("SELECT credential_ref,provider,revision,target_account,target_display,secret FROM external_credentials WHERE account_id=? ORDER BY credential_ref")
        .bind(agent.accountId).all<any>()).results;
    assert.deepEqual(rows.map(r => [r.credential_ref, r.target_account]), [['postiz:' + integrations[1].id, 'linkedin-page/' + integrations[1].id], ['postiz:' + integrations[0].id, 'tiktok/' + integrations[0].id]]);
    const sealed = rows[0].secret instanceof ArrayBuffer ? new Uint8Array(rows[0].secret) : Uint8Array.from(rows[0].secret);
    assert.ok(!Buffer.from(sealed).toString('latin1').includes(apiKey));
    assert.equal(await openSecret(key, {accountId: agent.accountId, credentialRef: rows[0].credential_ref, provider: 'postiz', revision: rows[0].revision}, sealed), apiKey);
    // Reconnecting with a key that no longer lists the LinkedIn page revokes it and rotates the TikTok revision.
    await withIntegrations(() => Response.json([integrations[0]]), async () => {
        assert.equal((await connectPostiz(env, {humanId, authAt: Date.now(), agentId: agent.accountId, apiKey})).ok, true);
    });
    const after = (await h.db.prepare("SELECT credential_ref,revision,revoked_at IS NOT NULL revoked,secret IS NULL erased FROM external_credentials WHERE account_id=? ORDER BY credential_ref")
        .bind(agent.accountId).all<any>()).results;
    assert.deepEqual(after.map(r => [r.revoked, r.erased]), [[1, 1], [0, 0]]);
    assert.notEqual(after[1].revision, rows[1].revision);
}));

test('connectPostiz_Refusals_NothingStored', () => scenario(async ({h, env}) => {
    const agent = await h.register(), humanId = await h.claim(agent);
    const input = {humanId, authAt: Date.now(), agentId: agent.accountId, apiKey};
    await withIntegrations(() => Response.json([]), async () => assert.deepEqual(await connectPostiz(env, input), {ok: false, reason: 'no-channel'}));
    await withIntegrations(() => Response.json(integrations), async () => {
        assert.deepEqual(await connectPostiz(env, {...input, apiKey: 'wrong-key-0000000000'}), {ok: false, reason: 'provider-refused'});
        assert.deepEqual(await connectPostiz(env, {...input, authAt: Date.now() - 11 * 60_000}), {ok: false, reason: 'stale-authentication'});
        assert.deepEqual(await connectPostiz(env, {...input, humanId: crypto.randomUUID()}), {ok: false, reason: 'not-found'});
    });
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_credentials').first<number>('n'), 0);
}));

test('proposePublishPost_PlanMustMatchChannelAndPinMedia', () => scenario(async ({h, env}) => {
    const {agent} = await connected(h, env);
    const image = await upload(h, agent, randomBytes(2000), 'image/png'), webm = await upload(h, agent, randomBytes(2000), 'video/webm');
    const tiktokRef = 'postiz:' + integrations[0].id, pageRef = 'postiz:' + integrations[1].id;
    for (const [ref, content] of [[pageRef, {text: 'x', media: [], settings: {__type: 'x', who_can_reply_post: 'everyone'}}],
        [tiktokRef, {text: '', media: [pin(image)], settings: {__type: 'tiktok', privacy_level: 'SELF_ONLY'}}],
        [pageRef, {text: 'x', media: [{...pin(image), sha256: 'f'.repeat(64)}], settings: {__type: 'linkedin-page'}}],
        [pageRef, {text: 'x', media: [pin(webm)], settings: {__type: 'linkedin-page'}}],
        [pageRef, {text: '  ', media: [], settings: {__type: 'linkedin-page'}}],
        [pageRef, {text: 'x', media: [], settings: {__type: 'linkedin-page'}, publishAt: '2001-01-01T00:00:00Z'}],
        [pageRef, {text: 'x', media: [], settings: {__type: 'linkedin-page'}, extra: 1}]] as const)
        assert.equal((await propose(h, agent, ref, content)).status, 400, JSON.stringify(content).slice(0, 100));
    assert.equal((await propose(h, agent, tiktokRef, {text: 'New beat', media: [pin(image)], settings: tiktok})).status, 200);
}));

test('executePublishPost_TextOnly_PostsOnceToTheApprovedChannel', () => scenario(async ({h, p, env}) => {
    const {agent, actionId} = await approvedPost(h, env, {text: 'Hiring a sound designer.', media: [], settings: {__type: 'linkedin-page'}});
    assert.equal(p.posts.length, 0, 'approval alone sends nothing');
    const first = await execute(h, agent, actionId), again = await execute(h, agent, actionId);
    assert.equal(first.status, 'completed');
    assert.equal(first.receipt, 'posted:post-1');
    assert.deepEqual(again, first);
    assert.equal(p.posts.length, 1);
    assert.equal(p.posts[0].type, 'now');
    assert.deepEqual(p.posts[0].posts, [{integration: {id: integrations[1].id}, value: [{content: 'Hiring a sound designer.', image: []}], settings: {__type: 'linkedin-page'}}]);
}));

test('executePublishPost_WithMedia_PostizFetchesExactBytesThroughSignedUrl', () => scenario(async ({h, p, env}) => {
    const bytes = randomBytes(150_000);
    const {agent, actionId} = await approvedPost(h, env, async (a: any) => ({text: 'New beat', media: [pin(await upload(h, a, bytes, 'video/mp4'))], settings: tiktok}),
        'postiz:' + integrations[0].id);
    const result = await execute(h, agent, actionId);
    assert.equal(result.status, 'completed', JSON.stringify(result));
    assert.equal(p.uploads.length, 1);
    assert.ok(p.fetched[0].equals(bytes));
    assert.deepEqual(p.posts[0].posts[0].value[0].image, [{id: 'media-1', path: 'https://uploads.postiz.com/' + createHash('sha256').update(bytes).digest('hex').slice(0, 8)}]);
    const signed = new URL(p.uploads[0]);
    signed.searchParams.set('signature', 'A'.repeat(43));
    assert.equal((await h.mf.dispatchFetch(signed.href)).status, 404, 'a tampered signature serves nothing');
    const other = new URL(p.uploads[0]); other.searchParams.set('account', crypto.randomUUID());
    assert.equal((await h.mf.dispatchFetch(other.href)).status, 404, 'the signature binds the agent');
}));

test('executePublishPost_Scheduled_SendsScheduleAtApprovedTime', () => scenario(async ({h, p, env}) => {
    const at = new Date(Date.now() + 3600_000).toISOString().replace(/\.\d{3}Z$/, '.000Z');
    const {agent, actionId} = await approvedPost(h, env, {text: 'Tonight 8pm', media: [], settings: {__type: 'linkedin-page'}, publishAt: at});
    assert.equal((await execute(h, agent, actionId)).receipt, 'scheduled:post-1');
    assert.equal(p.posts[0].type, 'schedule');
    assert.equal(p.posts[0].date, at);
}));

test('executePublishPost_PostizRefusesOrMediaFails_FailedWithNoPost', () => scenario(async ({h, p, env}) => {
    const refused = await approvedPost(h, env, {text: 'a', media: [], settings: {__type: 'linkedin-page'}});
    p.post = () => Response.json({message: 'bad settings'}, {status: 400});
    const result = await execute(h, refused.agent, refused.actionId);
    assert.equal(result.status, 'failed');
    assert.equal(result.failure, 'provider-rejected: postiz-refused-400');

    const media = await approvedPost(h, env, async (a: any) => ({text: 'b', media: [pin(await upload(h, a, randomBytes(500), 'image/jpeg'))], settings: {__type: 'linkedin-page'}}));
    p.upload = () => Response.json({message: 'Blocked IP'}, {status: 400});
    const posted = p.posts.length;
    const failed = await execute(h, media.agent, media.actionId);
    assert.equal(failed.failure, 'provider-rejected: media-upload-refused');
    assert.equal(p.posts.length, posted, 'no post is created when media did not reach Postiz');
}));

for (const [name, respond] of [['ServerError', () => new Response('{}', {status: 502})], ['ResponseLost', () => { throw Error('reset'); }],
    ['MalformedBody', () => Response.json({ok: true})]] as const)
    test(`executePublishPost_${name}_UnknownAndNeverResent`, () => scenario(async ({h, p, env}) => {
        const {agent, actionId} = await approvedPost(h, env, {text: 'c', media: [], settings: {__type: 'linkedin-page'}});
        p.post = respond;
        const first = await execute(h, agent, actionId);
        p.post = () => Response.json([{postId: 'late', integration: 'x'}]);
        const again = await execute(h, agent, actionId);
        assert.equal(first.status, 'unknown');
        assert.equal(again.status, 'unknown');
        assert.equal(p.posts.length, 1);
    }));
