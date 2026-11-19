import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { start, type AgentCredential } from './harness.ts';
import { openSecret } from '../src/credential-vault.ts';
import { connectAccount } from '../src/external-credentials.ts';
import { ownerAction, ownerOverview } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';

const key = randomBytes(32).toString('base64url');
const tenantId = '11111111-1111-4111-8111-111111111111';
const clientId = '22222222-2222-4222-8222-222222222222';
const conversationId = '19:teams-test@thread.tacv2';
const serviceUrl = 'https://smba.trafficmanager.net/amer/' + tenantId + '/';
const clientSecret = 'teams-client-secret-fixture', accessToken = 'teams-access-token-fixture', activityId = '1800000000123';
const fields = {tenantId, clientId, conversationId, serviceUrl, clientSecret};
const target = [tenantId, clientId, conversationId, serviceUrl].join('|');
const display = `Teams ${conversationId} / Bot ${clientId} / Tenant ${tenantId}`;
const credentialRef = 'teams:' + createHash('sha256').update(target).digest('base64url');
const tokenUrl = 'https://login.microsoftonline.com/' + tenantId + '/oauth2/v2.0/token';
const conversationUrl = serviceUrl + 'v3/conversations/' + encodeURIComponent(conversationId);
const membersUrl = conversationUrl + '/pagedmembers?pageSize=50';
const activityUrl = conversationUrl + '/activities';

type Call = {method: string; url: string; authorization: string | null; body: unknown};
type TeamsApi = {calls: Call[]; membersStatus: number; tokenStatus: number; activityResponse: () => Response};
type Scenario = Awaited<ReturnType<typeof start>> & {env: Env; agent: AgentCredential; humanId: string};

async function dispatch(api: TeamsApi, request: Request): Promise<Response> {
    const authorization = request.headers.get('authorization');
    const body = request.method === 'POST'
        ? request.headers.get('content-type') === 'application/x-www-form-urlencoded'
            ? Object.fromEntries(new URLSearchParams(await request.text())) : await request.json()
        : null;
    api.calls.push({method: request.method, url: request.url, authorization, body});
    if (request.url === tokenUrl) return Response.json({access_token: accessToken}, {status: api.tokenStatus});
    if (request.url === membersUrl) return Response.json({members: [{id: 'teams-test-member'}]}, {status: api.membersStatus});
    if (request.url === activityUrl) return api.activityResponse();
    return new Response('Unexpected outbound request', {status: 403});
}

async function scenario(run: (s: Scenario, api: TeamsApi) => Promise<void>) {
    const api: TeamsApi = {calls: [], membersStatus: 200, tokenStatus: 200, activityResponse: () => Response.json({id: activityId}, {status: 201})};
    const h = await start({bindings: {EXTERNAL_CREDENTIAL_KEY: key}, outboundService: (request: Request) => dispatch(api, request)});
    try {
        const agent = await h.register(), humanId = await h.claim(agent);
        await run({...h, env: {db: h.db, EXTERNAL_CREDENTIAL_KEY: key} as unknown as Env, agent, humanId}, api);
    } finally { await h.close(); }
}

async function connect(s: Scenario, api: TeamsApi, values = fields) {
    const original = globalThis.fetch;
    globalThis.fetch = (input, init) => {
        assert.equal(init?.redirect, 'manual');
        return dispatch(api, new Request(input, init));
    };
    try {
        return await connectAccount(s.env, {humanId: s.humanId, authAt: Date.now(), agentId: s.agent.accountId, provider: 'teams', fields: values});
    } finally { globalThis.fetch = original; }
}

const tokenCall = {method: 'POST', url: tokenUrl, authorization: null,
    body: {grant_type: 'client_credentials', client_id: clientId, client_secret: clientSecret, scope: 'https://api.botframework.com/.default'}};
const memberCall = {method: 'GET', url: membersUrl, authorization: 'Bearer ' + accessToken, body: null};
const activityCall = (text: string) => ({method: 'POST', url: activityUrl, authorization: 'Bearer ' + accessToken,
    body: {type: 'message', text, textFormat: 'markdown'}});

const execute = (s: Scenario, id: string) => s.ok('/v1/external-actions/' + id + '/execute', s.agent, 'POST', {});

async function approve(s: Scenario, text: string) {
    const content = JSON.stringify({text, media: []});
    const proposed = await s.ok('/v1/external-actions', s.agent, 'POST', {credentialRef, operation: 'publish_post', content});
    const id = proposed.actionId as string;
    assert.equal((await execute(s, id)).status, 'pending');
    const overview = await ownerOverview(s.env, s.humanId);
    assert.equal(overview.frozen, false);
    const plan = overview.externalActions.find(item => item.actionId === id);
    assert.ok(plan);
    assert.equal(plan.content, content);
    assert.equal(plan.targetAccount, target);
    assert.equal(plan.targetDisplay, display);
    assert.deepEqual(await ownerAction(s.env, {humanId: s.humanId, authAt: Date.now(), kind: 'confirm-action',
        requestId: id, planDigest: plan.planDigest}), {ok: true});
    return id;
}

test('connectTeams_VerifiesTheExactConversationAndSealsOnlyTheClientSecret', () => scenario(async (s, api) => {
    for (const invalidUrl of ['https://smba.trafficmanager.net.evil.test/teams/', 'https://user@smba.trafficmanager.net/teams/',
        'https://smba.trafficmanager.net:8443/teams/', 'https://smba.trafficmanager.net/amer/../teams/',
        'https://smba.trafficmanager.net/teams/?url=https://elsewhere.test']) {
        assert.deepEqual(await connect(s, api, {...fields, serviceUrl: invalidUrl}), {ok: false, reason: 'provider-refused'});
    }
    assert.deepEqual(await connect(s, api, {...fields, conversationId: '../wrong'}), {ok: false, reason: 'provider-refused'});
    assert.deepEqual(api.calls, []);

    api.membersStatus = 403;
    assert.deepEqual(await connect(s, api), {ok: false, reason: 'provider-refused'});
    assert.deepEqual(api.calls, [tokenCall, memberCall]);
    assert.deepEqual((await s.ok('/v1/external-credentials', s.agent)).items, []);

    api.membersStatus = 200;
    api.calls.length = 0;
    assert.deepEqual(await connect(s, api, {...fields, serviceUrl: serviceUrl.slice(0, -1)}), {ok: true, accounts: [display]});
    assert.deepEqual(api.calls, [tokenCall, memberCall]);
    const listed = (await s.ok('/v1/external-credentials', s.agent)).items;
    assert.equal(listed.length, 1);
    assert.equal(listed[0].credentialRef, credentialRef);
    assert.equal(listed[0].targetAccount, target);
    assert.equal(listed[0].mediaRules.max, 0);
    assert.ok(!JSON.stringify(listed).includes(clientSecret));
    const stored = await s.db.prepare('SELECT revision,secret FROM external_credentials WHERE account_id=? AND credential_ref=?')
        .bind(s.agent.accountId, credentialRef).first<{revision: string; secret: ArrayBuffer | number[]}>();
    assert.ok(stored);
    const sealed = stored.secret instanceof ArrayBuffer ? new Uint8Array(stored.secret) : Uint8Array.from(stored.secret);
    assert.ok(!Buffer.from(sealed).includes(clientSecret));
    assert.equal(await openSecret(key, {accountId: s.agent.accountId, credentialRef, provider: 'teams', revision: stored.revision}, sealed), clientSecret);
}));

test('executeTeams_ApprovedTextPostsOnceAndRecordsTheActivityId', () => scenario(async (s, api) => {
    assert.deepEqual(await connect(s, api), {ok: true, accounts: [display]});
    api.calls.length = 0;
    const text = '**Team update**\nApproved by the owner. 中文';
    const id = await approve(s, text);
    assert.deepEqual(api.calls, []);
    const result = await execute(s, id);
    assert.equal(result.status, 'completed');
    assert.equal(result.receipt, activityId);
    assert.deepEqual(await execute(s, id), result);
    assert.deepEqual(api.calls, [tokenCall, activityCall(text)]);
}));

test('executeTeams_TokenAndActivityRefusalsStopBeforeFurtherDispatch', () => scenario(async (s, api) => {
    assert.deepEqual(await connect(s, api), {ok: true, accounts: [display]});
    api.calls.length = 0;
    api.tokenStatus = 401;
    const text = 'Approved in Teams.';
    const tokenRefused = await approve(s, text);
    const rejected = await execute(s, tokenRefused);
    assert.equal(rejected.status, 'failed');
    assert.equal(rejected.failure, 'provider-rejected: teams-token-unavailable');
    assert.deepEqual(await execute(s, tokenRefused), rejected);
    assert.deepEqual(api.calls, [tokenCall]);

    api.calls.length = 0;
    api.tokenStatus = 200;
    api.activityResponse = () => Response.json({error: {message: clientSecret}}, {status: 403});
    const activityRefused = await approve(s, text);
    const refused = await execute(s, activityRefused);
    assert.equal(refused.status, 'failed');
    assert.equal(refused.failure, 'provider-rejected: teams-refused-403');
    assert.ok(!JSON.stringify(refused).includes(clientSecret));
    assert.deepEqual(await execute(s, activityRefused), refused);
    assert.deepEqual(api.calls, [tokenCall, activityCall(text)]);
}));

test('executeTeams_AmbiguousActivityStaysUnknownWithoutFollowingRedirectOrReplaying', () => scenario(async (s, api) => {
    assert.deepEqual(await connect(s, api), {ok: true, accounts: [display]});
    const ambiguous = [
        () => new Response(null, {status: 503}),
        () => Response.redirect('https://unapproved.example/activities', 307),
        () => Response.json({id: 'unconfirmed'}, {status: 202}),
        () => Response.json({}, {status: 201})
    ];
    for (const activityResponse of ambiguous) {
        api.calls.length = 0;
        api.activityResponse = activityResponse;
        const text = 'Approved Teams post with an uncertain response.';
        const id = await approve(s, text);
        const result = await execute(s, id);
        assert.equal(result.status, 'unknown');
        assert.equal(result.receipt, null);
        assert.deepEqual(await execute(s, id), result);
        assert.deepEqual(api.calls, [tokenCall, activityCall(text)]);
    }
}));
