import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, secret } from './harness.ts';
import { createOwnedAgent, humanHasAgent, listHumanAgents, maxAgentsPerHuman, signInHuman, type HumanSignIn } from '../src/humans.ts';
import type { Env } from '../src/database.ts';

const environment = (h: { db: unknown }, overrides: Partial<Env> = {}) => ({
    db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100',
    REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100', ...overrides }) as unknown as Env;
const github = (subject: string, displayLogin = 'alice', providerCreatedAt = '2020-01-01T00:00:00Z'): HumanSignIn =>
    ({ provider: 'github', subject, providerCreatedAt, displayLogin });
const humanId = async (env: Env, subject = '123') => {
    const result = await signInHuman(env, github(subject));
    assert.equal(result.ok, true);
    return (result as { humanId: string }).humanId;
};
const create = (env: Env, id: string, displayName = 'Claude Code') => createOwnedAgent(env, { humanId: id, displayName, sourceIp: '203.0.113.7' });

test('signInHuman_SameGithubId_ReturnsSameHumanAndUpdatesLogin', async () => {
    const h = await start();
    try {
        const env = environment(h);
        const first = await signInHuman(env, github('123', 'alice'));
        const second = await signInHuman(env, github('123', 'alice-renamed'));
        assert.deepEqual(second, first);
        assert.equal(first.ok, true);
        assert.equal(await h.db.prepare('SELECT display_login FROM humans WHERE subject=?').bind('123').first('display_login'), 'alice-renamed');
    } finally { await h.close(); }
});

test('signInHuman_DifferentIds_ReturnDifferentHumans', async () => {
    const h = await start();
    try {
        const env = environment(h);
        assert.notEqual(await humanId(env, '1'), await humanId(env, '2'));
    } finally { await h.close(); }
});

test('signInHuman_ProviderCreatedAtChanged_FreezesHuman', async () => {
    const h = await start();
    try {
        const env = environment(h);
        await humanId(env);
        assert.deepEqual(await signInHuman(env, github('123', 'alice', '2021-02-02T00:00:00Z')), { ok: false, reason: 'frozen' });
        assert.deepEqual(await signInHuman(env, github('123')), { ok: false, reason: 'frozen' });
        assert.notEqual(await h.db.prepare('SELECT frozen_at FROM humans WHERE subject=?').bind('123').first('frozen_at'), null);
    } finally { await h.close(); }
});

test('createOwnedAgent_NewHuman_AgentListedWithoutControl', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        const created = await create(env, id);
        assert.equal(created.ok, true);
        const accountId = (created as { accountId: string }).accountId;
        assert.deepEqual(await listHumanAgents(env, id), [{ accountId, displayName: 'Claude Code' }]);
        assert.equal(await humanHasAgent(env, id, accountId), true);
        const row = await h.db.prepare('SELECT human_id,control_hash,control_hash_scheme,control_hash_parameters FROM accounts WHERE account_id=?').bind(accountId).first();
        assert.deepEqual({ ...row }, { human_id: id, control_hash: null, control_hash_scheme: null, control_hash_parameters: null });
        const self = await h.register();
        const selfRow = await h.db.prepare('SELECT human_id,control_hash FROM accounts WHERE account_id=?').bind(self.accountId).first();
        assert.equal(selfRow.human_id, null);
        assert.ok(selfRow.control_hash);
        assert.equal((await h.request('/v1/addresses', secret())).status, 401);
    } finally { await h.close(); }
});

test('createOwnedAgent_EleventhAgent_ReturnsAgentLimit', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        for (let i = 0; i < maxAgentsPerHuman; i++) assert.equal((await create(env, id, 'agent ' + i)).ok, true);
        assert.deepEqual(await create(env, id), { ok: false, reason: 'agent-limit' });
        assert.equal((await listHumanAgents(env, id)).length, 10);
    } finally { await h.close(); }
});

test('createOwnedAgent_ConcurrentAtNine_OnlyOneSucceeds', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        for (let i = 0; i < 9; i++) assert.equal((await create(env, id, 'agent ' + i)).ok, true);
        const results = await Promise.all([create(env, id, 'left'), create(env, id, 'right')]);
        assert.equal(results.filter(r => r.ok).length, 1);
        assert.deepEqual(results.find(r => !r.ok), { ok: false, reason: 'agent-limit' });
        assert.equal((await listHumanAgents(env, id)).length, 10);
    } finally { await h.close(); }
});

test('listHumanAgents_ManyAgents_OldestFirst', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        for (const name of ['first', 'second', 'third']) await create(env, id, name);
        assert.deepEqual((await listHumanAgents(env, id)).map(a => a.displayName), ['first', 'second', 'third']);
    } finally { await h.close(); }
});

test('humanHasAgent_OtherHumansAgent_ReturnsFalse', async () => {
    const h = await start();
    try {
        const env = environment(h), owner = await humanId(env, '1'), other = await humanId(env, '2');
        const created = await create(env, owner) as { accountId: string };
        assert.equal(await humanHasAgent(env, other, created.accountId), false);
        assert.equal(await humanHasAgent(env, owner, created.accountId), true);
    } finally { await h.close(); }
});

test('createOwnedAgent_InvalidName_ReturnsInvalid', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        for (const name of ['', 'a'.repeat(129), 'a\u0007']) assert.deepEqual(await create(env, id, name), { ok: false, reason: 'invalid' });
        assert.deepEqual(await listHumanAgents(env, id), []);
    } finally { await h.close(); }
});

test('createOwnedAgent_SourceIpOverHourlyLimit_ReturnsRateLimited', async () => {
    const h = await start();
    try {
        const env = environment(h, { REGISTRATION_PER_IP_PER_HOUR: '1' }), id = await humanId(env);
        assert.equal((await create(env, id)).ok, true);
        assert.deepEqual(await create(env, id), { ok: false, reason: 'rate-limited' });
    } finally { await h.close(); }
});
