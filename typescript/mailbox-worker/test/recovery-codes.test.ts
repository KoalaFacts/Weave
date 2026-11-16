import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start } from './harness.ts';
import { agentKey, dpopProof, type TestKey } from './agent-key.ts';
import { createOwnedAgent, signInHuman } from '../src/humans.ts';
import { issueRecoveryCode, recoverAgentKey, recoveryCodeTicks } from '../src/recovery-codes.ts';
import { epochSeconds, nowUtc } from '../src/time.ts';
import { Problem } from '../src/wire.ts';
import type { Env } from '../src/database.ts';

type Harness = Awaited<ReturnType<typeof start>>;
type Agent = {agentId: string; humanId: string};

const recoveryUrl = 'http://localhost/v1/key-requests';
const dayTicks = 864_000_000_000n;

function env(h: Harness): Env {
    return {db: h.db, REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '200', REGISTRATION_PER_IP_PER_HOUR: '100', REGISTRATION_GLOBAL_PER_HOUR: '100'} as unknown as Env;
}
async function human(h: Harness, subject: string): Promise<string> {
    const signedIn = await signInHuman(env(h), {provider: 'github', subject, providerCreatedAt: '2020-01-01T00:00:00Z', displayLogin: 'person' + subject});
    assert.equal(signedIn.ok, true);
    return (signedIn as {humanId: string}).humanId;
}
async function owned(h: Harness, subject: string): Promise<Agent> {
    const humanId = await human(h, subject);
    const agent = await createOwnedAgent(env(h), {humanId, displayName: 'owned', sourceIp: '203.0.113.7'});
    assert.equal(agent.ok, true);
    return {agentId: (agent as {accountId: string}).accountId, humanId};
}
async function issued(h: Harness, agent: Agent, at = nowUtc()): Promise<string> {
    const result = await issueRecoveryCode(env(h), agent, () => at);
    assert.equal(result.ok, true, JSON.stringify(result));
    return (result as {code: string}).code;
}
/** Calls the handler directly on a pinned clock, with a proof signed at that instant. */
function recover(h: Harness, key: TestKey, body: unknown, at = nowUtc()): Promise<{kid: string; coolingUntil: string | null}> {
    return recoverAgentKey(env(h), new Request(recoveryUrl, {method: 'POST', headers: {'CF-Connecting-IP': '198.51.100.' + (1 + Math.floor(Math.random() * 250)),
        DPoP: dpopProof(key, {method: 'POST', url: recoveryUrl, iat: epochSeconds(at)})}}), body, () => at);
}
async function refused(outcome: Promise<unknown>, status: number, code: string, reason?: string): Promise<void> {
    const error = await outcome.then(() => assert.fail('expected a refusal'), (e: unknown) => e);
    assert.ok(error instanceof Problem, String(error));
    assert.deepEqual([error.status, error.code], [status, code]);
    if (reason !== undefined) assert.deepEqual(error.detail, {field: 'proof', reason});
}
/** Inserts a live key row created `age` ticks ago, outside the 24-hour addition window when older than a day. */
async function seedKey(h: Harness, accountId: string, key: TestKey = agentKey(), age = 2n * dayTicks): Promise<TestKey> {
    const created = (nowUtc() - age).toString();
    await h.db.prepare('INSERT INTO agent_keys(kid,account_id,public_jwk,created_at,updated_at) VALUES(?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))')
        .bind(key.kid, accountId, JSON.stringify(key.publicJwk), created, created).run();
    return key;
}
const codeUsed = async (h: Harness, agentId: string) => Number(await h.db.prepare('SELECT used_at IS NOT NULL used FROM recovery_codes WHERE account_id=?').bind(agentId).first('used'));
const keyRows = async (h: Harness, agentId: string) => Number(await h.db.prepare('SELECT count(*) n FROM agent_keys WHERE account_id=?').bind(agentId).first('n'));

test('recovery_CodeExpiry_AcceptedOneTickBeforeRefusedFromExactTick', async () => {
    const h = await start();
    try {
        const agent = await owned(h, '1'), issuedAt = nowUtc(), expiresAt = issuedAt + recoveryCodeTicks;
        const late = await issued(h, agent, issuedAt);
        await refused(recover(h, agentKey(), {agentId: agent.agentId, code: late}, expiresAt), 404, 'unavailable');
        assert.equal(await codeUsed(h, agent.agentId), 0);
        const inTime = await issued(h, agent, issuedAt);
        assert.equal((await recover(h, agentKey(), {agentId: agent.agentId, code: inTime}, expiresAt - 1n)).coolingUntil, null);
        assert.equal(await codeUsed(h, agent.agentId), 1);
    } finally { await h.close(); }
});

test('recovery_AgentNowOwnedByAnotherHuman_404NothingWritten', async () => {
    const h = await start();
    try {
        const agent = await owned(h, '1'), other = await human(h, '2'), code = await issued(h, agent);
        await h.db.prepare('UPDATE accounts SET human_id=? WHERE account_id=?').bind(other, agent.agentId).run();
        await refused(recover(h, agentKey(), {agentId: agent.agentId, code}), 404, 'unavailable');
        assert.deepEqual([await codeUsed(h, agent.agentId), await keyRows(h, agent.agentId)], [0, 0]);
        // The new owner's code replaces the old one and binds to the new owner.
        const fresh = await issued(h, {agentId: agent.agentId, humanId: other});
        await recover(h, agentKey(), {agentId: agent.agentId, code: fresh});
        assert.deepEqual([await codeUsed(h, agent.agentId), await keyRows(h, agent.agentId)], [1, 1]);
    } finally { await h.close(); }
});

test('recovery_CodeReissuedAfterUse_NewCodeAccepted', async () => {
    const h = await start();
    try {
        const agent = await owned(h, '1');
        await recover(h, agentKey(), {agentId: agent.agentId, code: await issued(h, agent)});
        const again = await issued(h, agent);
        assert.equal(await codeUsed(h, agent.agentId), 0);
        await recover(h, agentKey(), {agentId: agent.agentId, code: again});
        assert.deepEqual([await codeUsed(h, agent.agentId), await keyRows(h, agent.agentId)], [1, 2]);
    } finally { await h.close(); }
});

test('recovery_AgentsOwnRevokedKey_409KeyInUseCodeKept', async () => {
    const h = await start();
    try {
        const agent = await owned(h, '1'), key = await seedKey(h, agent.agentId), code = await issued(h, agent);
        await h.db.prepare('UPDATE agent_keys SET revoked_at=updated_at WHERE kid=?').bind(key.kid).run();
        await refused(recover(h, key, {agentId: agent.agentId, code}), 409, 'conflict', 'key-in-use');
        assert.equal(await codeUsed(h, agent.agentId), 0);
        assert.equal(Number(await h.db.prepare('SELECT revoked_at IS NOT NULL r FROM agent_keys WHERE kid=?').bind(key.kid).first('r')), 1);
    } finally { await h.close(); }
});

test('recovery_AgentsOwnLiveKey_200SpendsCodeAddsNoRow', async () => {
    const h = await start();
    try {
        const agent = await owned(h, '1'), key = await seedKey(h, agent.agentId), code = await issued(h, agent);
        assert.equal((await recover(h, key, {agentId: agent.agentId, code, label: 'ignored'})).kid, key.kid);
        assert.deepEqual([await codeUsed(h, agent.agentId), await keyRows(h, agent.agentId)], [1, 1]);
        assert.equal(await h.db.prepare('SELECT label FROM agent_keys WHERE kid=?').bind(key.kid).first('label'), null);
    } finally { await h.close(); }
});

test('recovery_AtHundredLiveKeys_429CapacityCodeKept', async () => {
    const h = await start();
    try {
        const agent = await owned(h, '1');
        for (let i = 0; i < 99; i++) await seedKey(h, agent.agentId);
        // The 100th live key is admitted...
        await recover(h, agentKey(), {agentId: agent.agentId, code: await issued(h, agent)});
        // ...a 101st is not, and the code stays unspent.
        const code = await issued(h, agent);
        await refused(recover(h, agentKey(), {agentId: agent.agentId, code}), 429, 'capacity');
        assert.deepEqual([await codeUsed(h, agent.agentId), await keyRows(h, agent.agentId)], [0, 100]);
    } finally { await h.close(); }
});
