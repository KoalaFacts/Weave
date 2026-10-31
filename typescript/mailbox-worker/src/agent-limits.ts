import type { Transaction } from './database.ts';
import { Problem, fail } from './wire.ts';

export type AgentTier = 'claimed' | 'unclaimed';
export type HourlyPolicy = 'agent-send' | 'agent-contact' | 'agent-token';
export type HourlyAdmission = {admitted: true} | {admitted: false; retryAfter: number};

/** Unclaimed agents (no owning human) run under lower limits than claimed ones. */
export const agentLimits: Record<AgentTier, {liveAddresses: number; publicAddresses: number; messagesPerHour: number; contactRequestsPerHour: number; tokensPerHour: number}> = {
    unclaimed: {liveAddresses: 3, publicAddresses: 1, messagesPerHour: 30, contactRequestsPerHour: 10, tokensPerHour: 30},
    claimed: {liveAddresses: 20, publicAddresses: 5, messagesPerHour: 300, contactRequestsPerHour: 60, tokensPerHour: 120},
};
export const keyAdditionsPerDay = 10;
export const keyRowsPerAgent = 1000;

const ticksPerHour = 36000000000n;
const dayTicks = 864000000000n;
const ticksPerSecond = 10000000n;

/** Read in the caller's transaction, so the tier and the counted write share one snapshot. */
export async function agentTier(tx: Transaction, accountId: string): Promise<AgentTier> {
    const owner = await tx.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(accountId).first<{human_id: string | null}>();
    if (!owner) fail(401, 'unauthenticated');
    return owner.human_id === null ? 'unclaimed' : 'claimed';
}

/**
 * Counts one action per agent in fixed UTC hours through the shared rate_limits counters. A refusal stages nothing; an admission
 * stages the increment and a bounded deletion of this policy's windows from earlier hours.
 */
export async function admitHourly(tx: Transaction, policy: HourlyPolicy, accountId: string, max: number): Promise<HourlyAdmission> {
    const at = tx.store.now, hour = Number(at / ticksPerHour);
    const counter = await tx.get(policy, '', accountId);
    const count = counter?.hour === hour ? counter.count : 0;
    if (count >= max) {
        const remaining = (BigInt(hour) + 1n) * ticksPerHour - at;
        return {admitted: false, retryAfter: Math.max(1, Number((remaining + ticksPerSecond - 1n) / ticksPerSecond))};
    }
    tx.put(policy, '', accountId, {hour, count: count + 1});
    tx.writes.push(tx.db.prepare('DELETE FROM rate_limits WHERE seq IN (SELECT seq FROM rate_limits WHERE policy=? AND window_start<CAST(? AS INTEGER) LIMIT 100)')
        .bind(policy, (BigInt(hour) * ticksPerHour).toString()));
    return {admitted: true};
}

export async function requireHourly(tx: Transaction, policy: HourlyPolicy, accountId: string, max: number): Promise<void> {
    const admission = await admitHourly(tx, policy, accountId, max);
    if (!admission.admitted) throw new Problem(429, 'capacity', admission.retryAfter);
}

/**
 * Bounds key churn: at most `keyAdditionsPerDay` key additions per rolling 24 h and `keyRowsPerAgent` key rows, live and revoked.
 * An addition is an `agent_keys` row or an unconfirmed `key_requests` row; a confirmed request is already counted by the key row it
 * created. Owner-authorised recovery passes `countRequests: false`, so key requests made with the agent's own (possibly stolen)
 * keys cannot exhaust the owner's way back in; recovered keys still count as additions.
 */
export async function requireKeyChurnRoom(tx: Transaction, accountId: string, {countRequests = true}: {countRequests?: boolean} = {}): Promise<void> {
    const since = (tx.store.now - dayTicks).toString();
    const counts = await tx.db.prepare(`SELECT (SELECT count(*) FROM agent_keys WHERE account_id=?1) total,
        (SELECT count(*) FROM agent_keys WHERE account_id=?1 AND created_at>CAST(?2 AS INTEGER))
        + CASE WHEN ?3 THEN (SELECT count(*) FROM key_requests WHERE account_id=?1 AND status<>'confirmed' AND created_at>CAST(?2 AS INTEGER)) ELSE 0 END recent`)
        .bind(accountId, since, countRequests ? 1 : 0).first<{total: number; recent: number}>();
    if (!counts) fail(503, 'storage');
    if (counts.total >= keyRowsPerAgent || counts.recent >= keyAdditionsPerDay) fail(429, 'capacity');
}
