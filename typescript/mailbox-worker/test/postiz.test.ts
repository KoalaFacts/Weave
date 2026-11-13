import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { sweepPublications, firstRecheckMs } from '../src/external-publications.ts';
import { connectAccount } from '../src/external-credentials.ts';
import { postiz } from '../src/postiz-provider.ts';
const connectPostiz = (env: Env, {apiKey, ...owner}: {humanId: string; authAt: number; agentId: string; apiKey: string}) =>
    connectAccount(env, {...owner, provider: 'postiz', fields: {apiKey}});
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

type Postiz = {posts: any[]; uploads: string[]; fetched: Buffer[]; listed: URL[]; post: () => Response; upload: (bytes: Buffer) => Response; list: () => Response};
async function scenario(run: (s: {h: Harness; p: Postiz; env: Env}) => Promise<void>) {
    const p: Postiz = {posts: [], uploads: [], fetched: [], listed: [], list: () => Response.json({posts: []}),
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
        if (request.method === 'GET' && request.url.startsWith(api + '/posts?')) { p.listed.push(new URL(request.url)); return p.list(); }
        return new Response('refused', {status: 599});
    }});
    try { await run({h, p, env: environment(h)}); } finally { await h.close(); }
}

/** Replaces fetch for Postiz's channel listing while `run` executes (connecting runs in this process). */
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
            {ok: true, accounts: ['tiktok · HSC Beats (@hscbeats)', 'linkedin-page · Koala Facts (@koalafacts)']});
    });
    return {agent, humanId};
}
const tiktok = {__type: 'tiktok', privacy_level: 'PUBLIC_TO_EVERYONE', duet: false, stitch: false, comment: true, autoAddMusic: 'no',
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

// ---- publication tracking -------------------------------------------------------------------------------------------
const listing = (posts: Record<string, unknown>[]) => () => Response.json({posts});
const publication = (h: Harness, actionId: string) => h.db.prepare(`SELECT state,url,error,checks,CAST(checked_at AS TEXT) checked_at,
    CAST(next_check_at AS TEXT) next_check_at,CAST(due_at AS TEXT) due_at FROM external_publications WHERE action_id=?`).bind(actionId).first<any>();
/** Runs the sweep in this process, answering Postiz's post listing with `list`; returns the listing URLs it requested. */
async function sweepWith(env: Env, list: () => Response, at: number) {
    const original = globalThis.fetch, asked: URL[] = [];
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = new URL(String(input));
        assert.equal(init?.method ?? 'GET', 'GET', 'tracking only reads');
        assert.equal(new Headers(init?.headers).get('Authorization'), apiKey);
        assert.equal(url.origin + url.pathname, api + '/posts');
        asked.push(url);
        return list();
    };
    try { await sweepPublications(env, at); } finally { globalThis.fetch = original; }
    return asked;
}
const ms = (ticks: string) => Number(BigInt(ticks) / 10000n);

test('publication_ImmediatePost_TrackedThenPublishedWithUrl', () => scenario(async ({h, env}) => {
    const {agent, actionId} = await approvedPost(h, env, {text: 'Live', media: [], settings: {__type: 'linkedin-page'}});
    const executed = await execute(h, agent, actionId);
    assert.deepEqual(executed.publication, {state: 'queued', url: null, error: null, checkedAt: null});
    const tracked = await publication(h, actionId);
    const due = ms(tracked.due_at);
    assert.ok(due > Date.now() && due <= Date.now() + 3 * 60_000, 'first asked shortly after posting');

    assert.deepEqual(await sweepWith(env, listing([]), due - 1000), [], 'nothing is asked before it is due');
    const asked = await sweepWith(env, listing([{id: 'post-1', state: 'PUBLISHED', releaseURL: 'https://www.linkedin.com/feed/update/1', error: null}]), due + 1000);
    assert.equal(asked.length, 1);
    assert.ok(Date.parse(asked[0].searchParams.get('startDate')!) < due && Date.parse(asked[0].searchParams.get('endDate')!) > due, 'the listing covers the post');
    const read = await h.ok(`/v1/external-actions/${actionId}`, agent);
    assert.equal(read.status, 'completed');
    assert.deepEqual([read.publication.state, read.publication.url, read.publication.error], ['published', 'https://www.linkedin.com/feed/update/1', null]);
    assert.ok(read.publication.checkedAt);
    assert.equal((await publication(h, actionId)).next_check_at, null, 'a final state is never asked again');
    assert.deepEqual(await sweepWith(env, listing([]), due + 10 * 24 * 3600_000), []);
}));

test('publication_PlatformRefuses_FailedWithBoundedVisibleError', () => scenario(async ({h, env}) => {
    const {agent, actionId} = await approvedPost(h, env, {text: 'Live', media: [], settings: {__type: 'linkedin-page'}});
    await execute(h, agent, actionId);
    const due = ms((await publication(h, actionId)).due_at);
    await sweepWith(env, listing([{id: 'post-1', state: 'ERROR', releaseURL: 'https://x.example/', error: 'Token\u0000 expired\u202e ' + 'x'.repeat(400)}]), due + 1000);
    const read = (await h.ok(`/v1/external-actions/${actionId}`, agent)).publication;
    assert.equal(read.state, 'failed');
    assert.equal(read.url, null, 'a failed post has no URL');
    assert.ok(read.error.startsWith('Token expired x') && read.error.length === 300, read.error);
}));

test('publication_PlatformErrorCutInsideSurrogatePair_ErrorStaysWellFormed', () => scenario(async ({h, env}) => {
    const {agent, actionId} = await approvedPost(h, env, {text: 'Live', media: [], settings: {__type: 'linkedin-page'}});
    await execute(h, agent, actionId);
    const due = ms((await publication(h, actionId)).due_at);
    await sweepWith(env, listing([{id: 'post-1', state: 'ERROR', error: 'Lone \udc00 ' + 'x'.repeat(292) + '\u{1F600}'.repeat(5)}]), due + 1000);
    const read = (await h.ok(`/v1/external-actions/${actionId}`, agent)).publication;
    assert.equal(read.state, 'failed');
    assert.ok(read.error.isWellFormed(), 'no half surrogate pair is stored');
    assert.ok(read.error.startsWith('Lone \uFFFD x') && read.error.length === 299, String(read.error.length));
}));

test('publication_StillQueuedOrUnreachable_BacksOffWithoutChangingState', () => scenario(async ({h, env}) => {
    const {agent, actionId} = await approvedPost(h, env, {text: 'Live', media: [], settings: {__type: 'linkedin-page'}});
    await execute(h, agent, actionId);
    const due = ms((await publication(h, actionId)).due_at);
    await sweepWith(env, listing([{id: 'post-1', state: 'QUEUE', releaseURL: null, error: null}]), due);
    let row = await publication(h, actionId);
    assert.deepEqual([row.state, row.checks, ms(row.next_check_at)], ['queued', 1, due + firstRecheckMs * 2 ** 0]);
    await sweepWith(env, listing([]), ms(row.next_check_at));
    row = await publication(h, actionId);
    assert.deepEqual([row.state, row.checks], ['queued', 2], 'a post Postiz no longer lists stays queued');
    const at = ms(row.next_check_at);
    await sweepWith(env, () => new Response('down', {status: 500}), at);
    row = await publication(h, actionId);
    assert.deepEqual([row.state, row.checks, ms(row.next_check_at)], ['queued', 2, at + firstRecheckMs * 4], 'an unanswered check postpones without counting');
    // After the tracking window the post is left queued and never asked about again.
    await sweepWith(env, listing([]), due + 8 * 24 * 3600_000);
    assert.equal((await publication(h, actionId)).next_check_at, null);
}));

test('publication_LongQueued_BackoffCappedAtSixHours', () => scenario(async ({h, env}) => {
    const {agent, actionId} = await approvedPost(h, env, {text: 'Live', media: [], settings: {__type: 'linkedin-page'}});
    await execute(h, agent, actionId);
    const due = ms((await publication(h, actionId)).due_at);
    // Ten unanswered checks would double the wait to 15 min × 2^10; it stays at 6 hours.
    await h.db.prepare('UPDATE external_publications SET checks=10 WHERE action_id=?').bind(actionId).run();
    await sweepWith(env, listing([{id: 'post-1', state: 'QUEUE', releaseURL: null, error: null}]), due);
    const row = await publication(h, actionId);
    assert.deepEqual([row.state, row.checks, ms(row.next_check_at)], ['queued', 11, due + 6 * 3600_000]);
}));

test('publication_SixAgentsDue_FiveAskedPerRunTheRestNextRun', () => scenario(async ({h, env}) => {
    const tracked: {actionId: string; due: number}[] = [];
    for (let i = 0; i < 6; i++) {
        const {agent, actionId} = await approvedPost(h, env, {text: 'Live ' + i, media: [], settings: {__type: 'linkedin-page'}});
        await execute(h, agent, actionId);
        tracked.push({actionId, due: ms((await publication(h, actionId)).due_at)});
    }
    // Postiz allows 30 calls an hour: one run asks for at most five agents, earliest due first.
    const at = Math.max(...tracked.map(t => t.due)) + 1000;
    const published = listing(tracked.map((_, i) => ({id: 'post-' + (i + 1), state: 'PUBLISHED', releaseURL: null})));
    assert.equal((await sweepWith(env, published, at)).length, 5);
    const states = await Promise.all(tracked.map(async t => (await publication(h, t.actionId))));
    assert.deepEqual(states.map(s => s.state), ['published', 'published', 'published', 'published', 'published', 'queued']);
    assert.deepEqual([states[5].checks, ms(states[5].next_check_at)], [0, tracked[5].due], 'the agent left out is neither counted nor postponed');
    assert.equal((await sweepWith(env, published, at)).length, 1, 'the next run asks for it');
    assert.equal((await publication(h, tracked[5].actionId)).state, 'published');
}));

test('publication_SeveralPostsOneAgent_OneListingCall', () => scenario(async ({h, env}) => {
    const {agent, humanId} = await connected(h, env);
    const ids: string[] = [];
    for (const text of ['one', 'two']) {
        const {actionId} = await (await propose(h, agent, 'postiz:' + integrations[1].id, {text, media: [], settings: {__type: 'linkedin-page'}})).json() as {actionId: string};
        await approve(h, env, humanId, actionId);
        await execute(h, agent, actionId);
        ids.push(actionId);
    }
    const due = Math.max(...await Promise.all(ids.map(async id => ms((await publication(h, id)).due_at))));
    const asked = await sweepWith(env, listing([{id: 'post-1', state: 'PUBLISHED', releaseURL: null}, {id: 'post-2', state: 'ERROR', error: 'Duplicate'}]), due + 1000);
    assert.equal(asked.length, 1);
    assert.deepEqual(await Promise.all(ids.map(async id => (await publication(h, id)).state)), ['published', 'failed']);
}));

test('publication_CredentialDisconnected_TrackingStopsWithoutAsking', () => scenario(async ({h, env}) => {
    const {agent, humanId, actionId} = await approvedPost(h, env, {text: 'Live', media: [], settings: {__type: 'linkedin-page'}});
    await execute(h, agent, actionId);
    const due = ms((await publication(h, actionId)).due_at);
    assert.deepEqual(await ownerAction(env, {humanId, authAt: 0, kind: 'revoke-credential', agentId: agent.accountId, credentialRef: 'postiz:' + integrations[1].id} as OwnerAction), {ok: true});
    assert.deepEqual(await sweepWith(env, listing([]), due + 1000), []);
    const row = await publication(h, actionId);
    assert.deepEqual([row.state, row.next_check_at], ['queued', null]);
}));

test('publication_ScheduledSweep_WorkerCronAsksPostiz', () => scenario(async ({h, p, env}) => {
    const {agent, actionId} = await approvedPost(h, env, {text: 'Live', media: [], settings: {__type: 'linkedin-page'}});
    await execute(h, agent, actionId);
    // Make it due now so the Worker's own cron run picks it up.
    await h.db.prepare('UPDATE external_publications SET next_check_at=CAST(? AS INTEGER) WHERE action_id=?').bind((BigInt(Date.now() - 1000) * 10000n).toString(), actionId).run();
    p.list = listing([{id: 'post-1', state: 'PUBLISHED', releaseURL: 'https://www.linkedin.com/feed/update/2'}]);
    assert.equal((await (await h.mf.getWorker()).scheduled({cron: '*/5 * * * *', scheduledTime: Date.now()})).outcome, 'ok');
    assert.equal(p.listed.length, 1);
    assert.equal((await publication(h, actionId)).url, 'https://www.linkedin.com/feed/update/2');
}));

// ---- Postiz's own confirmation checks, through the adapter and connector with Postiz's answers replaced ----------------
/** Calls the adapter directly with Postiz's answers replaced; `answer` receives each request and returns the response. */
async function publishDirect(answer: (request: Request) => Response | Promise<Response>, media = 0) {
    const original = globalThis.fetch, sent: {method: string; path: string; redirect?: RequestRedirect}[] = [];
    globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
        sent.push({method: init?.method ?? 'GET', path: new URL(String(input)).pathname.replace('/public/v1', ''), redirect: init?.redirect});
        return answer(new Request(String(input), init));
    };
    const file = {mediaId: 'm', bytes: 300, sha256: 'a'.repeat(64), contentType: 'image/png', alt: ''};
    try {
        return {sent, outcome: await postiz.publish({env: {EXTERNAL_CREDENTIAL_KEY: key, EXTERNAL_MEDIA_ORIGIN: 'http://localhost'} as unknown as Env,
            accountId: 'a', targetAccount: 'linkedin-page/' + integrations[1].id, secret: apiKey, actionId: 'p',
            post: {text: 'x', media: Array.from({length: media}, () => file), settings: {__type: 'linkedin-page'}, publishAt: null}} as never)
            .catch((error: Error) => ({thrown: error.message}))};
    } finally { globalThis.fetch = original; }
}
/** Postiz's usual answers: an uploaded file for each upload and one created post. */
const usualPostiz = (overrides: {upload?: () => Response; post?: () => Response} = {}) => (request: Request) =>
    new URL(request.url).pathname.endsWith('/upload-from-url') ? overrides.upload?.() ?? Response.json({id: 'media-1', path: 'https://uploads.postiz.com/a.png'})
        : overrides.post?.() ?? Response.json([{postId: 'post-1', integration: integrations[1].id}]);
const notUtf8 = (json: string) => new Response(Buffer.concat([Buffer.from(json.slice(0, -3)), Buffer.from([0xff]), Buffer.from(json.slice(-3))]));
const calls = (sent: {method: string; path: string}[]) => sent.map(({method, path}) => method + ' ' + path);

test('publishPostizPost_AnswerNotOneConfirmedPost_UnconfirmedNeverCompleted', async () => {
    const one = [{postId: 'post-1', integration: integrations[1].id}];
    for (const [name, post] of [['a created post on a server error', () => Response.json(one, {status: 500})],
        ['two created posts', () => Response.json([...one, {postId: 'post-2', integration: integrations[1].id}])],
        ['a post id that is not Postiz\'s', () => Response.json([{postId: '../posts', integration: integrations[1].id}])],
        ['a post id in an answer over 256 KiB', () => Response.json([{...one[0], padding: 'x'.repeat(270_000)}])],
        ['an answer that is not UTF-8', () => notUtf8(JSON.stringify([{...one[0], note: 'abc'}]))]] as const) {
        const {outcome, sent} = await publishDirect(usualPostiz({post}));
        assert.deepEqual(outcome, {thrown: 'Unconfirmed Postiz response.'}, name + ' does not confirm one post, so the router records unknown');
        assert.deepEqual(calls(sent), ['POST /posts'], name);
    }
    const control = await publishDirect(usualPostiz());
    assert.equal((control.outcome as {receipt?: string}).receipt, 'posted:post-1', 'positive control: one post with a Postiz id');
    assert.deepEqual(control.sent.map(call => call.redirect), ['manual'], 'a redirect from Postiz is never followed');
});

test('publishPostizPost_DefiniteRefusal_RejectedWithTheStatus', async () => {
    for (const status of [400, 401, 403, 422, 429]) {
        const {outcome} = await publishDirect(usualPostiz({post: () => Response.json({message: 'no'}, {status})}));
        assert.deepEqual(outcome, {status: 'rejected', reason: 'postiz-refused-' + status});
    }
});

test('publishPostizPost_UploadNotConfirmed_MediaUploadRefusedAndNothingPosted', async () => {
    for (const [name, upload] of [['an uploaded file on a server error', () => Response.json({id: 'media-1', path: 'https://uploads.postiz.com/a.png'}, {status: 500})],
        ['an upload with no id', () => Response.json({id: 7, path: 'https://uploads.postiz.com/a.png'})],
        ['an upload outside https', () => Response.json({id: 'media-1', path: 'http://uploads.postiz.com/a.png'})]] as const) {
        const {outcome, sent} = await publishDirect(usualPostiz({upload}), 1);
        assert.deepEqual(outcome, {status: 'rejected', reason: 'media-upload-refused'}, name);
        assert.deepEqual(calls(sent), ['POST /upload-from-url'], name + ': nothing is posted');
    }
    const control = await publishDirect(usualPostiz(), 1);
    assert.equal((control.outcome as {status: string}).status, 'completed', 'positive control');
    assert.deepEqual(control.sent.map(call => call.redirect), ['manual', 'manual']);
});

test('connectPostiz_ChannelsNotConfirmedByPostiz_RefusedOrLeftOut', async () => {
    const verify = async (value: string, respond: () => Response) => {
        const original = globalThis.fetch; let asked = 0;
        globalThis.fetch = async (_input: RequestInfo | URL, init?: RequestInit) => { asked++; assert.equal(init?.redirect, 'manual'); return respond(); };
        try { return {channels: await postiz.connector!.verify({apiKey: value}), asked}; } finally { globalThis.fetch = original; }
    };
    assert.deepEqual(await verify(apiKey, () => Response.json(integrations, {status: 500})), {channels: null, asked: 1}, 'a listing on a server error');
    assert.deepEqual(await verify('short key', () => Response.json(integrations)), {channels: null, asked: 0}, 'a value that is not a Postiz key is never sent');
    const {channels} = await verify(apiKey, () => Response.json([
        {...integrations[1], id: 'cm4/../../posts'}, {...integrations[1], id: 'cm4inherited00000000000001', identifier: 'toString'},
        {...integrations[0], name: 'N'.repeat(200), profile: 'p'.repeat(200)}]));
    assert.deepEqual(channels, [{ref: integrations[0].id, targetAccount: 'tiktok/' + integrations[0].id,
        targetDisplay: 'tiktok · ' + 'N'.repeat(120) + ' (@' + 'p'.repeat(80) + ')'}], 'an id Postiz would not issue and an inherited name are left out; long labels are cut');
});

test('publicationStates_ListingOrUrlNotConfirmed_NotTrustedAsPostiz', async () => {
    const original = globalThis.fetch;
    const item = {receipt: 'posted:post-1', trackFrom: Date.now()};
    const ask = async (respond: () => Response) => {
        globalThis.fetch = async () => respond();
        try { return await postiz.publicationStates!(apiKey, [item] as never).catch((error: Error) => ({thrown: error.message})); } finally { globalThis.fetch = original; }
    };
    const published = (releaseURL: string) => ({posts: [{id: 'post-1', state: 'PUBLISHED', releaseURL}]});
    assert.deepEqual(await ask(() => Response.json(published('https://www.linkedin.com/feed/update/1'), {status: 500})), {thrown: 'Postiz did not list posts.'}, 'a listing on a server error');
    assert.deepEqual(await ask(() => Response.json({posts: {id: 'post-1'}})), {thrown: 'Postiz did not list posts.'}, 'posts that are not a list');
    for (const [name, url] of [['outside https', 'http://www.linkedin.com/feed/update/1'], ['over 2048 characters', 'https://www.linkedin.com/' + 'a'.repeat(2048)]] as const)
        assert.deepEqual(await ask(() => Response.json(published(url))), new Map([[item.receipt, {state: 'published', url: null, error: null}]]), name);
    assert.deepEqual(await ask(() => Response.json(published('https://www.linkedin.com/feed/update/1'))),
        new Map([[item.receipt, {state: 'published', url: 'https://www.linkedin.com/feed/update/1', error: null}]]), 'positive control');
});
