import { admitRegistration } from './accounts.ts';
import { atomic, type Env, type Transaction } from './database.ts';
import { limits, type Limits } from './limits.ts';
import { nowUtc, utcTicks } from './time.ts';
import { visibleName } from './visible-text.ts';
import { fail } from './wire.ts';

// A Human is a person who signed in with an external identity provider and owns
// agents. Owned agents are created without an agent key; their access is granted later.
/** Agents per page wherever a human's agents are listed. Small enough that a page's agent IDs fit one D1 statement's bound parameters. */
export const agentPageSize = 20;

export type HumanSignIn = { provider: 'github'; subject: string; providerCreatedAt: string; displayLogin: string };
export type AgentSummary = { accountId: string; displayName: string | null };
/** One page of a human's agents, oldest first; `next` is the cursor for the following page, null on the last. */
export type AgentPage = { agents: AgentSummary[]; next: string | null };
export type SignInResult = { ok: true; humanId: string } | { ok: false; reason: 'frozen' | 'unavailable' };
export type CreateAgentResult = { ok: true; accountId: string } | { ok: false; reason: 'agent-limit' | 'rate-limited' | 'invalid' | 'frozen' | 'unavailable' };

const subjectPattern = /^[1-9][0-9]{0,19}$/;
const loginPattern = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// Same gates the fetch handler applies: maintenance stops everything, recovery read-only stops writes.
export const writesRefused = (env: Env) => env.MAINTENANCE_MODE === 'true' || env.RECOVERY_READ_ONLY === 'true';
export const requireReadable = (env: Env) => { if (env.MAINTENANCE_MODE === 'true') fail(503, 'maintenance', 30); };

function sameInstant(left: string, right: string): boolean {
    try { return utcTicks(left) === utcTicks(right); } catch { return false; }
}
function requireUuid(value: unknown): string {
    if (typeof value !== 'string' || !uuidPattern.test(value)) fail();
    return value;
}

export async function signInHuman(env: Env, input: HumanSignIn): Promise<SignInResult> {
    if (writesRefused(env)) return { ok: false, reason: 'unavailable' };
    if (input?.provider !== 'github' || typeof input.subject !== 'string' || !subjectPattern.test(input.subject)
        || typeof input.displayLogin !== 'string' || !loginPattern.test(input.displayLogin)
        || typeof input.providerCreatedAt !== 'string') fail();
    try { utcTicks(input.providerCreatedAt); } catch { fail(); }
    return atomic(env.db, async tx => {
        const row = await tx.db.prepare('SELECT human_id,provider_created_at,display_login,frozen_at FROM humans WHERE provider=? AND subject=?')
            .bind(input.provider, input.subject).first<{ human_id: string; provider_created_at: string; display_login: string; frozen_at: string | null }>();
        const now = nowUtc().toString();
        if (!row) {
            const humanId = crypto.randomUUID();
            tx.writes.push(tx.db.prepare('INSERT INTO humans(human_id,provider,subject,provider_created_at,display_login,frozen_at,created_at,updated_at) VALUES(?,?,?,?,?,NULL,?,?)')
                .bind(humanId, input.provider, input.subject, input.providerCreatedAt, input.displayLogin, now, now));
            return { ok: true as const, humanId };
        }
        if (row.frozen_at !== null) return { ok: false as const, reason: 'frozen' as const };
        // A different account creation time means the provider reused the numeric ID.
        if (!sameInstant(row.provider_created_at, input.providerCreatedAt)) {
            tx.writes.push(tx.db.prepare('UPDATE humans SET frozen_at=?,updated_at=? WHERE human_id=?').bind(now, now, row.human_id));
            return { ok: false as const, reason: 'frozen' as const };
        }
        if (row.display_login !== input.displayLogin)
            tx.writes.push(tx.db.prepare('UPDATE humans SET display_login=?,updated_at=? WHERE human_id=?').bind(input.displayLogin, now, row.human_id));
        return { ok: true as const, humanId: row.human_id };
    });
}

/**
 * A page of the agents a (not frozen) human owns, oldest first, after the agent `after` names. A cursor that is not one of
 * this human's agents yields an empty page, never another human's agents.
 */
export async function listHumanAgents(env: Env, humanId: string, after: string | null = null): Promise<AgentPage> {
    requireReadable(env); requireUuid(humanId); if (after !== null) requireUuid(after);
    return atomic(env.db, async tx => {
        const frozen = await tx.db.prepare('SELECT frozen_at FROM humans WHERE human_id=?').bind(humanId).first<{ frozen_at: string | null }>();
        if (!frozen || frozen.frozen_at !== null) return { agents: [], next: null };
        const rows = await ownedAgentPage(tx, humanId, after);
        return { agents: rows.page.map(row => ({ accountId: row.account_id, displayName: row.display_name })), next: rows.next };
    });
}

/** The keyset page behind every list of a human's agents: `agentPageSize` rows after the cursor, and the cursor of the next page. */
export async function ownedAgentPage(tx: Transaction, humanId: string, after: string | null): Promise<{ page: { account_id: string; display_name: string | null }[]; next: string | null }> {
    const rows = (await tx.db.prepare(`SELECT account_id,display_name FROM accounts WHERE human_id=?${after === null ? '' : `
        AND (created_at,rowid)>(SELECT created_at,rowid FROM accounts WHERE account_id=? AND human_id=?)`} ORDER BY created_at,rowid LIMIT ?`)
        .bind(humanId, ...(after === null ? [] : [after, humanId]), agentPageSize + 1).all<{ account_id: string; display_name: string | null }>()).results;
    const page = rows.slice(0, agentPageSize);
    return { page, next: rows.length > agentPageSize ? page[page.length - 1]!.account_id : null };
}

export async function humanHasAgent(env: Env, humanId: string, accountId: string): Promise<boolean> {
    requireReadable(env); requireUuid(humanId); requireUuid(accountId);
    return await env.db.prepare('SELECT 1 n FROM accounts a JOIN humans h ON h.human_id=a.human_id WHERE a.human_id=? AND a.account_id=? AND h.frozen_at IS NULL').bind(humanId, accountId).first('n') !== null;
}

/** `check` runs first inside the write transaction, for a caller whose authority must still hold at commit (an owner grant). */
export async function createOwnedAgent(env: Env, input: { humanId: string; displayName: string; sourceIp: string },
    check?: (tx: Transaction) => Promise<void>, limit: Limits = limits): Promise<CreateAgentResult> {
    if (writesRefused(env)) return { ok: false, reason: 'unavailable' };
    const invalid = { ok: false, reason: 'invalid' } as const;
    if (typeof input?.humanId !== 'string' || !uuidPattern.test(input.humanId) || typeof input.sourceIp !== 'string'
        // Bidi or invisible characters could spoof a name in the OAuth agent picker; an invisible-only name must never
        // consume an owner's agent quota.
        || typeof input.displayName !== 'string' || input.displayName.length > 128 || !visibleName(input.displayName)) return invalid;
    return atomic(env.db, async tx => {
        await check?.(tx);
        const human = await tx.db.prepare('SELECT frozen_at FROM humans WHERE human_id=?').bind(input.humanId).first<{ frozen_at: string | null }>();
        if (!human) return invalid;
        // A frozen GitHub identity has lost its management rights; its agents are not deleted.
        if (human.frozen_at !== null) return { ok: false as const, reason: 'frozen' as const };
        const owned = await tx.db.prepare('SELECT count(*) n FROM accounts WHERE human_id=?').bind(input.humanId).first<number>('n');
        if (owned === null || owned >= limit.ownedAgentsPerHuman) return { ok: false as const, reason: 'agent-limit' as const };
        if (!await admitRegistration(tx, input.sourceIp, limit)) return { ok: false as const, reason: 'rate-limited' as const };
        const accountId = crypto.randomUUID();
        if (await tx.get('account', '', accountId)) fail(409, 'conflict');
        tx.put('account', '', accountId, { accountId, humanId: input.humanId, displayName: input.displayName, description: null });
        return { ok: true as const, accountId };
    });
}

export type RenameAgentResult = { ok: true } | { ok: false; reason: 'not-found' | 'invalid' | 'unavailable' };
/**
 * Renames an agent the human owns, after `check` inside the same transaction. Another human's agent, or none, is
 * `not-found`; the name follows the creation rules (1 to 128 visible characters).
 */
export async function renameOwnedAgent(env: Env, input: { humanId: string; agentId: string; displayName: string },
    check: (tx: Transaction) => Promise<void>): Promise<RenameAgentResult> {
    if (writesRefused(env)) return { ok: false, reason: 'unavailable' };
    if (typeof input?.humanId !== 'string' || !uuidPattern.test(input.humanId)) fail();
    if (typeof input.agentId !== 'string' || !uuidPattern.test(input.agentId)) return { ok: false, reason: 'not-found' };
    if (typeof input.displayName !== 'string' || input.displayName.length > 128 || !visibleName(input.displayName)) return { ok: false, reason: 'invalid' };
    return atomic(env.db, async tx => {
        await check(tx);
        const agent = await tx.get('account', '', input.agentId);
        if (!agent || agent.humanId !== input.humanId) return { ok: false as const, reason: 'not-found' as const };
        if (agent.displayName !== input.displayName) tx.put('account', '', input.agentId, { ...agent, displayName: input.displayName });
        return { ok: true as const };
    });
}
