import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { test } from 'node:test';
import { build } from 'esbuild';
import { start, testGrant } from './harness.ts';
import { ownerAction, ownerOverview, type OwnerAction } from '../src/owner-actions.ts';
import type { Env } from '../src/database.ts';
// Evaluate the bundled production module, as the other private-tool tests do.
const built = await build({entryPoints: ['src/remote-private.ts'], bundle: true, write: false, platform: 'node', format: 'esm', logLevel: 'silent'});
const {executePrivateMcp, privateMcpTools, privateScope} = await import('data:text/javascript;base64,' + Buffer.from(built.outputFiles[0].contents).toString('base64')) as typeof import('../src/remote-private.ts');

type Call = {operation: string; content: string; targetAccount: string; actionId: string};
const origin = 'https://demo.test';

async function scenario(run: (s: {h: Awaited<ReturnType<typeof start>>; env: Env; calls: Call[]; endpoint: string}) => Promise<void>) {
    const calls: Call[] = [];
    // The test-http provider posts here: a local fake that completes every call.
    const server = createServer(async (request, response) => {
        let text = ''; for await (const chunk of request) text += chunk;
        const call = JSON.parse(text) as Call;
        calls.push(call);
        response.writeHead(200, {'Content-Type': 'application/json'}).end(JSON.stringify({externalId: 'post-' + call.actionId}));
    }).listen(0, '127.0.0.1');
    await once(server, 'listening');
    const endpoint = `http://127.0.0.1:${(server.address() as {port: number}).port}/post`;
    const h = await start();
    const env = {db: h.db, CLEANUP_MAX_MESSAGES: '0', CLEANUP_TIME_BUDGET_MS: '0', EXTERNAL_ACTION_TEST_ENDPOINT: endpoint} as Env;
    try { await run({h, env, calls, endpoint}); } finally { await h.close(); server.close(); }
}

async function ownedWithCredential(h: Awaited<ReturnType<typeof start>>) {
    const agent = await h.register(), humanId = await h.claim(agent), at = (BigInt(Date.now()) * 10000n).toString();
    await h.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,created_at,updated_at)
        VALUES(?,'linkedin-main','test-http','rev-1','urn:li:person:alpha','Desk Alpha',CAST(? AS INTEGER),CAST(? AS INTEGER))`).bind(agent.accountId, at, at).run();
    return {agent, humanId, grantId: await testGrant(h.db, agent.accountId)};
}
const call = async (env: Env, accountId: string, grantId: string, name: string, args: Record<string, unknown>) => {
    const response = await executePrivateMcp(env, origin, accountId, name, args, grantId);
    return {status: response.status, body: await response.json() as Record<string, any>};
};

test('externalActionTools_Scopes_ReadAndWriteSeparatedAndExecuteIsOpenWorld', () => {
    assert.equal(privateScope('propose_external_action'), 'weave:actions.write');
    assert.equal(privateScope('execute_external_action'), 'weave:actions.write');
    assert.equal(privateScope('get_external_action'), 'weave:actions.read');
    const execute = privateMcpTools.find(tool => tool.name === 'execute_external_action')!;
    assert.equal(execute.annotations.openWorldHint, true);
    assert.equal(privateMcpTools.find(tool => tool.name === 'propose_external_action')!.annotations.openWorldHint, false);
});

test('externalActionTools_ProposeApproveExecute_SendsOnceThroughMcp', () => scenario(async ({h, env, calls}) => {
    const {agent, humanId, grantId} = await ownedWithCredential(h);
    const proposed = await call(env, agent.accountId, grantId, 'propose_external_action', {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Live now.'});
    assert.equal(proposed.status, 200, JSON.stringify(proposed.body));
    assert.equal(proposed.body.status, 'pending');
    const actionId = proposed.body.actionId as string;
    assert.equal((await call(env, agent.accountId, grantId, 'execute_external_action', {actionId})).body.status, 'pending');

    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    assert.deepEqual(await ownerAction(env, {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: actionId,
        planDigest: overview.externalActions[0].planDigest} as OwnerAction), {ok: true});
    assert.equal(calls.length, 0);

    const sent = await call(env, agent.accountId, grantId, 'execute_external_action', {actionId});
    const again = await call(env, agent.accountId, grantId, 'execute_external_action', {actionId});
    const read = await call(env, agent.accountId, grantId, 'get_external_action', {actionId});
    assert.equal(sent.body.status, 'completed');
    assert.equal(again.body.status, 'completed');
    assert.equal(read.body.receipt, 'post-' + actionId);
    assert.deepEqual(calls, [{operation: 'publish_text', content: 'Live now.', targetAccount: 'urn:li:person:alpha', actionId}]);
}));

test('externalActionTools_GrantRevokedAfterApproval_DoesNotSend', () => scenario(async ({h, env, calls}) => {
    const {agent, humanId, grantId} = await ownedWithCredential(h);
    const actionId = (await call(env, agent.accountId, grantId, 'propose_external_action', {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'x'})).body.actionId;
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    await ownerAction(env, {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: actionId, planDigest: overview.externalActions[0].planDigest} as OwnerAction);
    await h.db.prepare('UPDATE client_grants SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=?').bind((BigInt(Date.now()) * 10000n).toString(), grantId).run();

    const refused = await call(env, agent.accountId, grantId, 'execute_external_action', {actionId});
    assert.equal(refused.status, 401);
    assert.equal(calls.length, 0);
    assert.equal((await h.db.prepare('SELECT status FROM external_actions WHERE action_id=?').bind(actionId).first<string>('status')), 'approved');
}));

test('externalActionTools_InvalidArguments_RejectedBeforeAnyWrite', () => scenario(async ({h, env}) => {
    const {agent, grantId} = await ownedWithCredential(h);
    for (const [name, args] of [['propose_external_action', {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'x', accountId: 'other'}],
        ['propose_external_action', {credentialRef: 'linkedin-main', operation: 'publish_text'}], ['execute_external_action', {actionId: 'not-a-uuid'}],
        ['get_external_action', {actionId: crypto.randomUUID(), extra: 1}]] as const)
        assert.equal((await call(env, agent.accountId, grantId, name, args)).status, 400, name + ' ' + JSON.stringify(args));
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_actions').first<number>('n'), 0);
}));

test('externalActionTools_ProposeWithIdempotencyKey_OnePlanSharedWithTheHttpHeader', () => scenario(async ({h, env}) => {
    const {agent, humanId, grantId} = await ownedWithCredential(h);
    const args = {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Live now.', idempotencyKey: 'envoylet:post-7'};
    const first = await call(env, agent.accountId, grantId, 'propose_external_action', args);
    const again = await call(env, agent.accountId, grantId, 'propose_external_action', args);
    assert.equal(first.status, 200, JSON.stringify(first.body));
    assert.deepEqual(again.body, first.body);
    // The key is the agent's, whichever ingress sent it.
    const viaHttp = await h.fetchAs(agent, 'http://localhost/v1/external-actions', {method: 'POST',
        headers: {'Content-Type': 'application/json', 'Idempotency-Key': 'envoylet:post-7'},
        body: JSON.stringify({credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Live now.'})});
    assert.equal(viaHttp.status, 200, await viaHttp.clone().text());
    assert.equal((await viaHttp.json() as {actionId: string}).actionId, first.body.actionId);
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    assert.equal(overview.externalActions.length, 1);

    const changed = await call(env, agent.accountId, grantId, 'propose_external_action', {...args, content: 'Live now!'});
    assert.equal(changed.status, 409);
    assert.equal(changed.body.reason, 'idempotency-conflict');
    assert.equal((await call(env, agent.accountId, grantId, 'propose_external_action', {...args, idempotencyKey: 'not valid'})).status, 400);
    assert.equal(await h.db.prepare('SELECT count(*) n FROM external_actions').first<number>('n'), 1);
}));

test('listMyExternalActions_WaitingAndPast_SameEntriesAsTheByIdReadAndPagedWithBefore', () => scenario(async ({h, env}) => {
    const {agent, humanId, grantId} = await ownedWithCredential(h);
    const propose = async (content: string) => (await call(env, agent.accountId, grantId, 'propose_external_action', {credentialRef: 'linkedin-main', operation: 'publish_text', content})).body.actionId as string;
    const first = await propose('First.'), second = await propose('Second.'), third = await propose('Third.');
    const read = async (actionId: string) => (await call(env, agent.accountId, grantId, 'get_external_action', {actionId})).body;
    const list = async (args: Record<string, unknown>) => {
        const response = await call(env, agent.accountId, grantId, 'list_my_external_actions', args);
        assert.equal(response.status, 200, JSON.stringify(response.body));
        return response.body.actions as Record<string, unknown>[];
    };
    // Waiting plans, oldest first, by default and with waiting true.
    assert.deepEqual(await list({}), [await read(first), await read(second), await read(third)]);
    assert.deepEqual(await list({waiting: true}), await list({}));
    assert.deepEqual(await list({waiting: false}), []);

    // The first is approved and sent; the second expires. Both stop waiting.
    const overview = await ownerOverview(env, humanId) as Extract<Awaited<ReturnType<typeof ownerOverview>>, {frozen: false}>;
    const plan = overview.externalActions.find(item => item.actionId === first)!;
    assert.deepEqual(await ownerAction(env, {humanId, authAt: Date.now(), kind: 'confirm-action', requestId: first, planDigest: plan.planDigest} as OwnerAction), {ok: true});
    assert.equal((await call(env, agent.accountId, grantId, 'execute_external_action', {actionId: first})).body.status, 'completed');
    await h.db.prepare('UPDATE external_actions SET expires_at=created_at+1 WHERE action_id=?').bind(second).run();

    assert.deepEqual(await list({}), [await read(third)]);
    const past = await list({waiting: false});
    assert.deepEqual(past, [await read(second), await read(first)], 'newest proposal first, each as the by-ID read');
    assert.equal(past[1].status, 'completed');
    // before pages to the next older entries; the oldest ends the list.
    assert.deepEqual(await list({waiting: false, before: second}), [await read(first)]);
    assert.deepEqual(await list({waiting: false, before: first}), []);
    // A waiting plan of this agent is a valid cursor, as on HTTP.
    assert.deepEqual(await list({waiting: false, before: third}), past);

    // The same pages as the HTTP list.
    const viaHttp = await h.fetchAs(agent, `http://localhost/v1/external-actions?waiting=false&before=${second}`);
    assert.deepEqual((await viaHttp.json() as {actions: unknown[]}).actions, [await read(first)]);
}));

test('listMyExternalActions_InvalidArgumentsOrAnotherAgentsCursor_Refused', () => scenario(async ({h, env}) => {
    const {agent, grantId} = await ownedWithCredential(h);
    const other = await ownedWithCredential(h);
    const foreign = (await call(env, other.agent.accountId, other.grantId, 'propose_external_action', {credentialRef: 'linkedin-main', operation: 'publish_text', content: 'Theirs.'})).body.actionId as string;
    for (const [args, field] of [[{waiting: 'false'}, 'waiting'], [{waiting: null}, 'waiting'], [{before: foreign}, 'before'], [{waiting: true, before: foreign}, 'before'],
        [{waiting: false, before: 'not-a-uuid'}, 'before'], [{waiting: false, before: foreign.toUpperCase()}, 'before'], [{waiting: false, limit: 5}, 'limit']] as const) {
        const response = await call(env, agent.accountId, grantId, 'list_my_external_actions', args);
        assert.equal(response.status, 400, JSON.stringify(args));
        assert.equal(response.body.field, field, JSON.stringify(response.body));
    }
    // Another agent's action, or an unknown one, is unavailable as a cursor, like the by-ID read.
    for (const before of [foreign, crypto.randomUUID()]) {
        const response = await call(env, agent.accountId, grantId, 'list_my_external_actions', {waiting: false, before});
        assert.equal(response.status, 404, JSON.stringify(response.body));
        assert.equal(response.body.code, 'unavailable');
    }
    // A revoked grant is refused inside the read's own transaction, for both lists.
    await h.db.prepare('UPDATE client_grants SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=?').bind((BigInt(Date.now()) * 10000n).toString(), other.grantId).run();
    for (const args of [{}, {waiting: false}, {waiting: false, before: foreign}])
        assert.equal((await call(env, other.agent.accountId, other.grantId, 'list_my_external_actions', args)).status, 401, JSON.stringify(args));
    assert.equal(privateScope('list_my_external_actions'), 'weave:actions.read');
    assert.equal(privateMcpTools.find(tool => tool.name === 'list_my_external_actions')!.annotations.readOnlyHint, true);
}));
