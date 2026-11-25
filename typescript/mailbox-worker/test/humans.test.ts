import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.ts';
import { agentPageSize, createOwnedAgent, humanHasAgent, listHumanAgents, signInHuman, type HumanSignIn } from '../src/humans.ts';

const defaultOwnedLimit = 10;
const small = (overrides: Partial<Limits>): Limits => ({ ...limits, ownedAgentsPerHuman: defaultOwnedLimit, ...overrides });
import type { Env } from '../src/database.ts';
import { limits, type Limits } from '../src/limits.ts';

const environment = (h: { db: unknown }, overrides: Partial<Env> = {}) => ({
    db: h.db, ...overrides }) as unknown as Env;
const github = (subject: string, displayLogin = 'alice', providerCreatedAt = '2020-01-01T00:00:00Z'): HumanSignIn =>
    ({ provider: 'github', subject, providerCreatedAt, displayLogin });
const humanId = async (env: Env, subject = '123') => {
    const result = await signInHuman(env, github(subject));
    assert.equal(result.ok, true);
    return (result as { humanId: string }).humanId;
};
const create = (env: Env, id: string, displayName = 'Claude Code', limit: Limits = small({})) =>
    createOwnedAgent(env, { humanId: id, displayName, sourceIp: '203.0.113.7' }, undefined, limit);

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

test('signInHuman_SingleCharacterGithubLogin_Accepted', async () => {
    // GitHub permits 1–39 character logins. A valid one-letter identity must not
    // be rejected by Weave's private HumanDirectory before agent onboarding.
    const h = await start();
    try {
        const result = await signInHuman(environment(h), github('987', 'q'));
        assert.equal(result.ok, true);
        assert.equal(await h.db.prepare('SELECT display_login FROM humans WHERE subject=?')
            .bind('987').first('display_login'), 'q');
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

test('createOwnedAgent_NewHuman_AgentListedWithoutKey', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        const created = await create(env, id);
        assert.equal(created.ok, true);
        const accountId = (created as { accountId: string }).accountId;
        assert.deepEqual((await listHumanAgents(env, id)).agents, [{ accountId, displayName: 'Claude Code' }]);
        assert.equal(await humanHasAgent(env, id, accountId), true);
        assert.equal(await h.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(accountId).first('human_id'), id);
        assert.equal(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE account_id=?').bind(accountId).first('n'), 0);
        const self = await h.register();
        assert.equal(await h.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(self.accountId).first('human_id'), null);
        assert.equal((await h.request('/v1/addresses', h.stranger())).status, 401);
    } finally { await h.close(); }
});

test('createOwnedAgent_EleventhAgent_ReturnsAgentLimit', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        for (let i = 0; i < defaultOwnedLimit; i++) assert.equal((await create(env, id, 'agent ' + i)).ok, true);
        assert.deepEqual(await create(env, id), { ok: false, reason: 'agent-limit' });
        assert.equal((await listHumanAgents(env, id)).agents.length, 10);
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
        assert.equal((await listHumanAgents(env, id)).agents.length, 10);
    } finally { await h.close(); }
});

test('listHumanAgents_ManyAgents_OldestFirst', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        for (const name of ['first', 'second', 'third']) await create(env, id, name);
        assert.deepEqual((await listHumanAgents(env, id)).agents.map(a => a.displayName), ['first', 'second', 'third']);
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
        for (const name of ['', '   ', '\u00a0\u2003', 'a'.repeat(129), 'a\u0007']) assert.deepEqual(await create(env, id, name), { ok: false, reason: 'invalid' });
        assert.deepEqual((await listHumanAgents(env, id)).agents, []);
    } finally { await h.close(); }
});

test('createOwnedAgent_BidiFormattingControls_CannotSpoofAgentPickerName', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        // HTML escaping does not neutralize bidirectional rendering overrides
        // in an OAuth consent page's human-selected agent labels.
        for (const name of ['Safe\u202Ehidden', 'Safe\u2066hidden\u2069',
            'Safe\u200Fhidden', 'Safe\u061Chidden']) {
            assert.deepEqual(await create(env, id, name), { ok: false, reason: 'invalid' }, JSON.stringify(name));
        }
        assert.deepEqual((await listHumanAgents(env, id)).agents, []);
        // Legitimate multilingual names and emoji sequences must remain usable.
        assert.equal((await create(env, id, '合作助手 👩‍💻')).ok, true);
    } finally { await h.close(); }
});

test('createOwnedAgent_InvisibleDefaultIgnorables_CannotHideOrSpoofPickerName', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        // trim() does not remove zero-width space / word joiner; either can
        // make an owned-agent label visually blank or mask a changed name.
        for (const name of ['\u200b', '\u2060', 'Safe\u200bHidden',
            'Safe\u2060Hidden', 'Safe\u00adHidden', 'Safe\ufeffHidden']) {
            assert.deepEqual(await create(env, id, name), { ok: false, reason: 'invalid' },
                JSON.stringify(name));
        }
        assert.deepEqual((await listHumanAgents(env, id)).agents, [],
            'rejected invisible-name attempts must not consume the ten-agent quota');
        // Ordinary non-Latin text and the ZWJ in an emoji sequence still work.
        assert.equal((await create(env, id, 'مرحبا 👩‍💻')).ok, true);
    } finally { await h.close(); }
});

test('createOwnedAgent_OnlyInvisibleJoiners_CannotConsumeOwnerQuota', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env);
        // Legitimate emoji sequences need a ZWJ, but a name made entirely of
        // default-ignorable codepoints has no visible agent identity.
        for (const name of ['\u200d', '\u200c', '\ufe0f', '\u034f', ' \u200d\ufe0f ']) {
            assert.deepEqual(await create(env, id, name), { ok: false, reason: 'invalid' },
                JSON.stringify(name));
        }
        assert.equal(await h.db.prepare('SELECT count(*) AS n FROM accounts WHERE human_id=?')
            .bind(id).first('n'), 0, 'invisible-only names must not consume agent quota');
        assert.equal((await create(env, id, '合作助手 👩‍💻')).ok, true);
    } finally { await h.close(); }
});

test('createOwnedAgent_SourceIpOverHourlyLimit_ReturnsRateLimited', async () => {
    const h = await start();
    try {
        const env = environment(h, {}), id = await humanId(env), limit = small({ registrationPerIpPerHour: 1 });
        assert.equal((await create(env, id, 'first', limit)).ok, true);
        assert.deepEqual(await create(env, id, 'second', limit), { ok: false, reason: 'rate-limited' });
    } finally { await h.close(); }
});

test('createOwnedAgent_GlobalAccountCapReached_StillCreatesUpToPerHumanLimit', async () => {
    const h = await start();
    try {
        const env = environment(h, {}), id = await humanId(env), limit = small({ registrationMaxAccounts: 1 });
        await h.register();
        await h.register();
        for (let i = 0; i < defaultOwnedLimit; i++) assert.equal((await create(env, id, 'agent ' + i, limit)).ok, true);
        assert.deepEqual(await create(env, id, 'over', limit), { ok: false, reason: 'agent-limit' });
    } finally { await h.close(); }
});

test('humanDirectory_MaintenanceMode_RefusesWritesAndReads', async () => {
    const h = await start();
    try {
        const open = environment(h), id = await humanId(open), created = await create(open, id) as { accountId: string };
        const env = environment(h, { MAINTENANCE_MODE: 'true' });
        assert.deepEqual(await signInHuman(env, github('999')), { ok: false, reason: 'unavailable' });
        assert.deepEqual(await create(env, id), { ok: false, reason: 'unavailable' });
        await assert.rejects(listHumanAgents(env, id), { status: 503 });
        await assert.rejects(humanHasAgent(env, id, created.accountId), { status: 503 });
        assert.equal((await listHumanAgents(open, id)).agents.length, 1);
    } finally { await h.close(); }
});

test('humanDirectory_RecoveryReadOnly_RefusesWritesButAllowsReads', async () => {
    const h = await start();
    try {
        const open = environment(h), id = await humanId(open), created = await create(open, id) as { accountId: string };
        const env = environment(h, { RECOVERY_READ_ONLY: 'true' });
        assert.deepEqual(await signInHuman(env, github('999')), { ok: false, reason: 'unavailable' });
        assert.deepEqual(await create(env, id), { ok: false, reason: 'unavailable' });
        assert.equal((await listHumanAgents(env, id)).agents.length, 1);
        assert.equal(await humanHasAgent(env, id, created.accountId), true);
    } finally { await h.close(); }
});


test('humanDirectory_FrozenAfterCreatingAgent_RefusesManagement', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env), other = await humanId(env, '456');
        const created = await create(env, id) as { accountId: string };
        await h.db.prepare("UPDATE humans SET frozen_at=updated_at WHERE human_id=?").bind(id).run();
        assert.deepEqual((await listHumanAgents(env, id)).agents, []);
        assert.equal(await humanHasAgent(env, id, created.accountId), false);
        assert.deepEqual(await create(env, id, 'Another'), { ok: false, reason: 'frozen' });
        assert.equal(await h.db.prepare('SELECT count(*) n FROM accounts WHERE human_id=?').bind(id).first('n'), 1);
        // Over-correction guard: an unfrozen human is unaffected.
        assert.equal((await create(env, other)).ok, true);
    } finally { await h.close(); }
});

test('limits_Constants_MatchTheOwnerDecision', () => {
    assert.deepEqual({ ...limits }, { registrationMaxAccounts: 10000, registrationGlobalPerHour: 1000, registrationPerIpPerHour: 100, ownedAgentsPerHuman: 10000 });
    assert.equal(Object.isFrozen(limits), true);
});

test('createOwnedAgent_ConfiguredLimit_AllowsThatManyThenAgentLimit', async () => {
    const h = await start();
    try {
        const env = environment(h, {}), id = await humanId(env), limit = small({ ownedAgentsPerHuman: 25 });
        for (let i = 0; i < 25; i++) assert.equal((await create(env, id, 'agent ' + i, limit)).ok, true, 'agent ' + i);
        assert.deepEqual(await create(env, id, 'over', limit), { ok: false, reason: 'agent-limit' });
    } finally { await h.close(); }
});


test('listHumanAgents_MoreThanOnePage_PagesOldestFirstWithCursor', async () => {
    const h = await start();
    try {
        const env = environment(h, {}), id = await humanId(env);
        const created: string[] = [];
        for (let i = 0; i < agentPageSize + 5; i++) created.push((await create(env, id, 'agent ' + i, limits) as { accountId: string }).accountId);
        const first = await listHumanAgents(env, id);
        assert.deepEqual(first.agents.map(a => a.accountId), created.slice(0, agentPageSize));
        assert.equal(first.next, created[agentPageSize - 1]);
        const second = await listHumanAgents(env, id, first.next);
        assert.deepEqual(second.agents.map(a => a.accountId), created.slice(agentPageSize));
        assert.equal(second.next, null);
    } finally { await h.close(); }
});

test('listHumanAgents_CursorOfAnotherHumansAgent_EmptyPage', async () => {
    const h = await start();
    try {
        const env = environment(h), id = await humanId(env), other = await humanId(env, '456');
        // Theirs is older than mine, so a cursor read without the owner check would list mine after it.
        const theirs = await create(env, other, 'theirs') as { accountId: string };
        await create(env, id, 'mine');
        assert.deepEqual(await listHumanAgents(env, id, theirs.accountId), { agents: [], next: null });
        assert.deepEqual(await listHumanAgents(env, id, crypto.randomUUID()), { agents: [], next: null });
        await assert.rejects(listHumanAgents(env, id, 'not-a-uuid'), { status: 400 });
    } finally { await h.close(); }
});
