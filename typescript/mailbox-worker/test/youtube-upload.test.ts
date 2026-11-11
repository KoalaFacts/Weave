import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start } from './harness.ts';
import { sealSecret } from '../src/credential-vault.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
const key = randomBytes(32).toString('base64url');
const channel = 'UC' + 'b'.repeat(22), refreshToken = '1//refresh-upload', videoId = 'dQw4w9WgXcQ';
const sessionUrl = 'https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=session-1';
const bindings = {GOOGLE_CLIENT_ID: 'client.apps.googleusercontent.com', GOOGLE_CLIENT_SECRET: 'google-secret', EXTERNAL_CREDENTIAL_KEY: key};

type Google = {refresh: () => Response; init: () => Response; upload: (bytes: Buffer) => Response; uploads: Buffer[]; inits: Record<string, any>[]; refreshes: URLSearchParams[]};
function google(): Google {
    const g: Google = {uploads: [], inits: [], refreshes: [],
        refresh: () => Response.json({access_token: 'ya29.fresh', expires_in: 3599, token_type: 'Bearer'}),
        init: () => new Response(null, {status: 200, headers: {Location: sessionUrl}}),
        upload: () => Response.json({id: videoId, status: {privacyStatus: 'unlisted'}})};
    return g;
}
async function scenario(run: (s: {h: Harness; g: Google; env: Env}) => Promise<void>) {
    const g = google();
    const h = await start({bindings, outboundService: async (request: Request) => {
        if (request.method === 'POST' && request.url === 'https://oauth2.googleapis.com/token') { g.refreshes.push(new URLSearchParams(await request.text())); return g.refresh(); }
        if (request.headers.get('authorization') !== 'Bearer ya29.fresh') return new Response('{}', {status: 401, headers: {'Content-Type': 'application/json'}});
        if (request.method === 'POST' && request.url === 'https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status') {
            g.inits.push({body: await request.json(), length: request.headers.get('x-upload-content-length')}); return g.init();
        }
        if (request.method === 'PUT' && request.url === sessionUrl) { const bytes = Buffer.from(await request.arrayBuffer()); g.uploads.push(bytes); return g.upload(bytes); }
        return new Response('refused', {status: 599});
    }});
    const env = {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100', REGISTRATION_PER_IP_PER_HOUR: '100',
        REGISTRATION_GLOBAL_PER_HOUR: '100', ...bindings} as unknown as Env;
    try { await run({h, g, env}); } finally { await h.close(); }
}

async function connected(h: Harness, revision = 'rev-1') {
    const agent = await h.register(), humanId = await h.claim(agent), at = (BigInt(Date.now()) * 10000n).toString();
    const sealed = await sealSecret(key, {accountId: agent.accountId, credentialRef: 'youtube', provider: 'youtube', revision}, refreshToken);
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,secret,created_at,updated_at)
        VALUES(?,'youtube','youtube',?,?,'HSC Beats',?,CAST(? AS INTEGER),CAST(? AS INTEGER))`).bind(agent.accountId, revision, channel, sealed.buffer as ArrayBuffer, at, at).run();
    return {agent, humanId};
}
const upload = async (h: Harness, agent: {accountId: string}, bytes: Buffer, type = 'video/mp4') => h.fetchAs(agent as any, 'http://localhost/v1/external-media',
    {method: 'PUT', headers: {'Content-Type': type, 'Content-Length': String(bytes.length)}, body: bytes});
const plan = (media: {mediaId: string; bytes: number; sha256: string}, over: Record<string, unknown> = {}) =>
    JSON.stringify({title: 'Episode 12', description: 'Lyrics in the comments.', privacyStatus: 'unlisted', mediaId: media.mediaId, bytes: media.bytes, sha256: media.sha256, ...over});
const propose = (h: Harness, agent: any, content: string) => h.request('/v1/external-actions', agent, 'POST', {credentialRef: 'youtube', operation: 'upload_video', content});
const execute = (h: Harness, agent: any, actionId: string) => h.ok(`/v1/external-actions/${actionId}/execute`, agent, 'POST', {});

async function approvedUpload(h: Harness, env: Env, bytes = randomBytes(300_000)) {
    const {agent, humanId} = await connected(h);
    const stored = await upload(h, agent, bytes);
    assert.equal(stored.status, 200, await stored.clone().text());
    const media = await stored.json() as {mediaId: string; bytes: number; sha256: string};
    const proposed = await propose(h, agent, plan(media));
    assert.equal(proposed.status, 200, await proposed.clone().text());
    const action = await proposed.json() as {actionId: string};
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    assert.deepEqual(await ownerAction(env, {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: action.actionId,
        planDigest: overview.externalActions[0].planDigest} as OwnerAction), {ok: true});
    return {agent, humanId, media, bytes, actionId: action.actionId};
}

test('uploadMedia_StoresOnceAndReturnsSha256OfStoredBytes', () => scenario(async ({h}) => {
    const {agent} = await connected(h), bytes = randomBytes(70_000);
    const response = await upload(h, agent, bytes);
    assert.equal(response.status, 200, await response.clone().text());
    const media = await response.json() as {mediaId: string; bytes: number; sha256: string; contentType: string};
    assert.equal(media.bytes, bytes.length);
    assert.equal(media.sha256, createHash('sha256').update(bytes).digest('hex'));
    assert.equal(media.contentType, 'video/mp4');
    assert.equal((await upload(h, agent, bytes, 'text/html')).status, 400);
}));

test('executeExternalAction_YoutubeUpload_StreamsExactBytesOnceAndRecordsVideoId', () => scenario(async ({h, g, env}) => {
    const {agent, bytes, actionId} = await approvedUpload(h, env);
    assert.equal(g.uploads.length, 0, 'approval alone uploads nothing');
    const first = await execute(h, agent, actionId), again = await execute(h, agent, actionId);
    assert.equal(first.status, 'completed');
    assert.equal(first.receipt, videoId);
    assert.deepEqual(again, first);
    assert.equal(g.refreshes[0].get('refresh_token'), refreshToken, 'the sealed token is opened only for dispatch');
    assert.deepEqual(g.inits, [{body: {snippet: {title: 'Episode 12', description: 'Lyrics in the comments.'}, status: {privacyStatus: 'unlisted', selfDeclaredMadeForKids: false}},
        length: String(bytes.length)}]);
    assert.equal(g.uploads.length, 1);
    assert.ok(g.uploads[0].equals(bytes));
}));

test('proposeExternalAction_UploadPlanOutsideYoutubeLimits_NamesTheField', () => scenario(async ({h}) => {
    const {agent} = await connected(h);
    const media = await (await upload(h, agent, randomBytes(1000))).json() as {mediaId: string; bytes: number; sha256: string};
    const cases: [string, string][] = [
        ['not json', 'must be JSON {title, description, privacyStatus, mediaId, bytes, sha256}'],
        [plan(media, {extra: 1}), 'must be JSON {title, description, privacyStatus, mediaId, bytes, sha256}'],
        [plan(media, {title: '   '}), 'title must be a non-blank string'],
        [plan(media, {title: 7}), 'title must be a non-blank string'],
        [plan(media, {title: 't'.repeat(101)}), 'title must be at most 100 characters'],
        [plan(media, {title: 'a <b>'}), 'title must not contain < or >'],
        [plan(media, {description: null}), 'description must be a string'],
        [plan(media, {description: 'é'.repeat(2501)}), 'description must be at most 5000 bytes'],
        [plan(media, {description: 'x > y'}), 'description must not contain < or >'],
        [plan(media, {privacyStatus: 'everyone'}), 'privacyStatus must be one of "private", "unlisted", "public"'],
        [plan(media, {mediaId: 'nope'}), 'mediaId must be the ID that PUT /v1/external-media returned'],
        [plan(media, {mediaId: 7}), 'mediaId must be the ID that PUT /v1/external-media returned'],
        [plan(media, {bytes: 0}), 'bytes must be a positive whole number'],
        [plan(media, {bytes: 1.5}), 'bytes must be a positive whole number'],
        [plan(media, {bytes: '1000'}), 'bytes must be a positive whole number'],
        [plan(media, {sha256: 'F'.repeat(64)}), 'sha256 must be 64 lowercase hex characters'],
        [plan(media, {sha256: 'f'.repeat(63)}), 'sha256 must be 64 lowercase hex characters']];
    for (const [content, reason] of cases) {
        const response = await propose(h, agent, content);
        assert.equal(response.status, 400, content.slice(0, 80));
        const problem = await response.json() as {code: string; field: string; reason: string};
        assert.deepEqual({code: problem.code, field: problem.field, reason: problem.reason}, {code: 'invalid', field: 'content', reason}, content.slice(0, 80));
    }
    // Exactly at the limits is accepted: 100 characters of title, 5000 bytes of description.
    const atLimits = await propose(h, agent, plan(media, {title: 't'.repeat(100), description: 'é'.repeat(2500)}));
    assert.equal(atLimits.status, 200, await atLimits.clone().text());
}));

test('proposeExternalAction_UploadPlanNotPinnedToStoredMedia_Rejected', () => scenario(async ({h}) => {
    const {agent} = await connected(h), other = await connected(h);
    const media = await (await upload(h, agent, randomBytes(1000))).json() as {mediaId: string; bytes: number; sha256: string};
    const foreign = await (await upload(h, other.agent, randomBytes(1000))).json() as {mediaId: string; bytes: number; sha256: string};
    const image = await (await upload(h, agent, randomBytes(1000), 'image/png')).json() as {mediaId: string; bytes: number; sha256: string};
    const foreignImage = await (await upload(h, other.agent, randomBytes(1000), 'image/png')).json() as {mediaId: string; bytes: number; sha256: string};
    // A well-formed reference to media this agent has not stored, as given, keeps the reason apps match to upload again.
    const notStored = 'must name your uploaded video with its exact bytes and sha256';
    const cases: [string, string][] = [[plan({...media, sha256: 'f'.repeat(64)}), notStored], [plan({...media, bytes: media.bytes + 1}), notStored],
        [plan(foreign), notStored], [plan({...media, mediaId: crypto.randomUUID()}), notStored],
        [plan(image), 'mediaId must name a video, not an image'], [plan({...image, bytes: image.bytes + 1}), notStored],
        [plan(foreignImage), notStored]];
    for (const [content, reason] of cases) {
        const response = await propose(h, agent, content);
        assert.equal(response.status, 400, content.slice(0, 80));
        const problem = await response.json() as {code: string; field: string; reason: string};
        assert.deepEqual({code: problem.code, field: problem.field, reason: problem.reason}, {code: 'invalid', field: 'content', reason}, content.slice(0, 80));
    }
    for (const content of [plan(media, {privacyStatus: 'everyone'}), plan(media, {title: ''}), plan(media, {extra: 1}), 'not json'])
        assert.equal((await propose(h, agent, content)).status, 400, content.slice(0, 80));
}));

for (const [name, change, reason] of [
    ['TokenRefused', (g: Google) => { g.refresh = () => Response.json({error: 'invalid_grant'}, {status: 400}); }, 'provider-rejected: credential-refused'],
    ['UploadStartRefused', (g: Google) => { g.init = () => Response.json({error: {code: 403}}, {status: 403}); }, 'provider-rejected: upload-refused'],
] as const)
    test(`executeExternalAction_Youtube${name}_FailedWithNothingUploaded`, () => scenario(async ({h, g, env}) => {
        const {agent, actionId} = await approvedUpload(h, env);
        change(g);
        const result = await execute(h, agent, actionId);
        assert.equal(result.status, 'failed');
        assert.equal(result.failure, reason);
        assert.equal(g.uploads.length, 0);
    }));

for (const [name, respond] of [['ServerError', () => new Response('{}', {status: 503, headers: {'Content-Type': 'application/json'}})],
    ['ResponseLost', () => { throw Error('connection reset'); }], ['MalformedVideoId', () => Response.json({id: '<script>'})]] as const)
    test(`executeExternalAction_YoutubeUpload${name}_UnknownAndNeverReuploads`, () => scenario(async ({h, g, env}) => {
        const {agent, actionId} = await approvedUpload(h, env);
        g.upload = respond;
        const first = await execute(h, agent, actionId);
        g.upload = () => Response.json({id: videoId});
        const again = await execute(h, agent, actionId);
        assert.equal(first.status, 'unknown');
        assert.equal(again.status, 'unknown');
        assert.equal(g.uploads.length, 1);
    }));

test('executeExternalAction_SealedSecretDoesNotOpen_FailsWithoutCallingGoogle', () => scenario(async ({h, g, env}) => {
    const {agent, actionId} = await approvedUpload(h, env);
    // A ciphertext sealed for another revision, written under the approved revision, must not open.
    const foreign = await sealSecret(key, {accountId: agent.accountId, credentialRef: 'youtube', provider: 'youtube', revision: 'rev-other'}, refreshToken);
    await h.db.prepare("UPDATE external_credentials SET secret=? WHERE account_id=?").bind(foreign.buffer as ArrayBuffer, agent.accountId).run();
    const result = await execute(h, agent, actionId);
    assert.equal(result.status, 'failed');
    assert.equal(result.failure, 'credential-unreadable');
    assert.equal(g.refreshes.length + g.uploads.length, 0);
}));
