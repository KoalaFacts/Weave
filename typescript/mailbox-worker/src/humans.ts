import { admitRegistration } from './accounts.ts';
import { atomic, type Env } from './database.ts';
import { nowUtc, utcTicks } from './time.ts';
import { fail } from './wire.ts';

// A Human is a person who signed in with an external identity provider and owns
// agents. Owned agents carry no control secret; their access is granted later.
export const maxAgentsPerHuman = 10;

export type HumanSignIn = { provider: 'github'; subject: string; providerCreatedAt: string; displayLogin: string };
export type AgentSummary = { accountId: string; displayName: string | null };
export type SignInResult = { ok: true; humanId: string } | { ok: false; reason: 'frozen' | 'unavailable' };
export type CreateAgentResult = { ok: true; accountId: string } | { ok: false; reason: 'agent-limit' | 'rate-limited' | 'invalid' | 'frozen' | 'unavailable' };

const subjectPattern = /^[1-9][0-9]{0,19}$/;
const loginPattern = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const controlCharacters = /[\u0000-\u001f\u007f-\u009f]/u;

// Same gates the fetch handler applies: maintenance stops everything, recovery read-only stops writes.
const writesRefused = (env: Env) => env.MAINTENANCE_MODE === 'true' || env.RECOVERY_READ_ONLY === 'true';
const requireReadable = (env: Env) => { if (env.MAINTENANCE_MODE === 'true') fail(503, 'maintenance', 30); };

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

export async function listHumanAgents(env: Env, humanId: string): Promise<AgentSummary[]> {
    requireReadable(env); requireUuid(humanId);
    const { results } = await env.db.prepare('SELECT a.account_id,a.display_name FROM accounts a JOIN humans h ON h.human_id=a.human_id WHERE a.human_id=? AND h.frozen_at IS NULL ORDER BY a.created_at,a.rowid LIMIT ?')
        .bind(humanId, maxAgentsPerHuman).all<{ account_id: string; display_name: string | null }>();
    return results.map(row => ({ accountId: row.account_id, displayName: row.display_name }));
}

export async function humanHasAgent(env: Env, humanId: string, accountId: string): Promise<boolean> {
    requireReadable(env); requireUuid(humanId); requireUuid(accountId);
    return await env.db.prepare('SELECT 1 n FROM accounts a JOIN humans h ON h.human_id=a.human_id WHERE a.human_id=? AND a.account_id=? AND h.frozen_at IS NULL').bind(humanId, accountId).first('n') !== null;
}

export async function createOwnedAgent(env: Env, input: { humanId: string; displayName: string; sourceIp: string }): Promise<CreateAgentResult> {
    if (writesRefused(env) || env.REGISTRATION_ENABLED !== 'true') return { ok: false, reason: 'unavailable' };
    const invalid = { ok: false, reason: 'invalid' } as const;
    if (typeof input?.humanId !== 'string' || !uuidPattern.test(input.humanId) || typeof input.sourceIp !== 'string'
        || typeof input.displayName !== 'string' || input.displayName.length < 1 || input.displayName.length > 128
        || controlCharacters.test(input.displayName)) return invalid;
    return atomic(env.db, async tx => {
        const human = await tx.db.prepare('SELECT frozen_at FROM humans WHERE human_id=?').bind(input.humanId).first<{ frozen_at: string | null }>();
        if (!human) return invalid;
        // A frozen GitHub identity has lost its management rights; its agents are not deleted.
        if (human.frozen_at !== null) return { ok: false as const, reason: 'frozen' as const };
        const owned = await tx.db.prepare('SELECT count(*) n FROM accounts WHERE human_id=?').bind(input.humanId).first<number>('n');
        if (owned === null || owned >= maxAgentsPerHuman) return { ok: false as const, reason: 'agent-limit' as const };
        if (!await admitRegistration(env, tx, input.sourceIp)) return { ok: false as const, reason: 'rate-limited' as const };
        const accountId = crypto.randomUUID();
        if (await tx.get('account', '', accountId)) fail(409, 'conflict');
        tx.put('account', '', accountId, { accountId, humanId: input.humanId, controlHash: null, controlHashScheme: null, controlHashParameters: null, displayName: input.displayName, description: null });
        return { ok: true as const, accountId };
    });
}
