import { atomic, type Database, type Env, type Transaction } from './database.ts';
import { writesRefused } from './humans.ts';
import { nowUtc } from './time.ts';
import { replaceInvisible } from './visible-text.ts';
import { fail } from './wire.ts';
import type { LiveGrant } from './remote-oauth.ts';

// The mailbox mirror of each OAuth consent grant. The authorization server records a grant before it issues an
// authorization code; every private /mcp call then requires the token's grant row to be live, so a revocation takes
// effect on the next call without waiting for issued access or refresh tokens to expire.

export type GrantRow = { grantId: string; humanId: string; accountId: string; clientId: string };
/** `clientName` is the client's self-reported name; optional so callers that do not send it keep working. */
export type RecordGrantInput = GrantRow & { clientName?: string | null };
export type RecordGrantResult = { ok: true } | { ok: false; reason: 'not-found' | 'frozen' | 'unavailable' };
export type RevokeGrantInput = { humanId: string; grantId: string };
export type RevokeGrantResult = { ok: true } | { ok: false; reason: 'not-found' | 'unavailable' };
/** An owner grant lets one client manage every agent its human owns, including agents the human owns later. `appDecides`
 * is the owner's choice, made on Weave's consent page and off unless given as true, to also let that client approve or
 * reject the pending plans of those agents (A1); it is recorded in `owner_grant_approvals` in the same transaction. */
export type RecordOwnerGrantInput = { grantId: string; humanId: string; clientId: string; clientName?: string | null; appDecides?: boolean };
export type RecordOwnerGrantResult = { ok: true } | { ok: false; reason: 'not-found' | 'frozen' | 'grant-limit' | 'unavailable' };
export type OwnerGrantRow = { grantId: string; humanId: string; clientId: string };
/** Live owner grants one human may hold at once; the owner page lists every one of them. */
export const maxLiveOwnerGrants = 20;

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const controlCharacters = /[\u0000-\u001f\u007f-\u009f]/u;
const maxClientName = 128;
const uuid = (value: unknown): value is string => typeof value === 'string' && uuidPattern.test(value);

/**
 * The self-reported name bounded to 128 characters with control, bidi and invisible characters replaced by `?`; empty
 * or whitespace-only becomes null. The authorization server applies the same rule (auth-worker/src/client-name.ts).
 */
function boundedClientName(value: unknown): string | null {
    if (value === undefined || value === null) return null;
    if (typeof value !== 'string') fail();
    if (value.trim() === '') return null;
    return [...replaceInvisible(value, '?')].slice(0, maxClientName).join('');
}

/** Records a grant for an agent the (not frozen) human owns. The authorization server issues no code unless this returns ok. */
export async function recordGrant(env: Env, input: RecordGrantInput, clock: () => bigint = nowUtc): Promise<RecordGrantResult> {
    if (writesRefused(env)) return { ok: false, reason: 'unavailable' };
    if (!uuid(input?.grantId) || !uuid(input.humanId) || !uuid(input.accountId) || typeof input.clientId !== 'string'
        || input.clientId.length < 1 || input.clientId.length > 2048 || controlCharacters.test(input.clientId)) fail();
    const clientName = boundedClientName(input.clientName);
    return atomic(env.db, async tx => {
        const human = await tx.db.prepare('SELECT frozen_at,(SELECT 1 FROM accounts WHERE account_id=? AND human_id=humans.human_id) owned FROM humans WHERE human_id=?')
            .bind(input.accountId, input.humanId).first<{ frozen_at: string | null; owned: number | null }>();
        if (!human) return { ok: false as const, reason: 'not-found' as const };
        if (human.frozen_at !== null) return { ok: false as const, reason: 'frozen' as const };
        if (human.owned === null) return { ok: false as const, reason: 'not-found' as const };
        tx.writes.push(tx.db.prepare('INSERT INTO client_grants(grant_id,human_id,account_id,client_id,client_name,created_at,revoked_at) VALUES(?,?,?,?,?,CAST(? AS INTEGER),NULL)')
            .bind(input.grantId, input.humanId, input.accountId, input.clientId, clientName, tx.store.now.toString()));
        return { ok: true as const };
    }, clock);
}

/** Records an owner grant for a (not frozen) human. The authorization server issues no code unless this returns ok. */
export async function recordOwnerGrant(env: Env, input: RecordOwnerGrantInput, clock: () => bigint = nowUtc): Promise<RecordOwnerGrantResult> {
    if (writesRefused(env)) return { ok: false, reason: 'unavailable' };
    if (!uuid(input?.grantId) || !uuid(input.humanId) || typeof input.clientId !== 'string'
        || input.clientId.length < 1 || input.clientId.length > 2048 || controlCharacters.test(input.clientId)
        || (input.appDecides !== undefined && typeof input.appDecides !== 'boolean')) fail();
    const clientName = boundedClientName(input.clientName);
    return atomic(env.db, async tx => {
        const human = await tx.db.prepare('SELECT frozen_at,(SELECT count(*) FROM owner_grants WHERE human_id=humans.human_id AND revoked_at IS NULL) live FROM humans WHERE human_id=?')
            .bind(input.humanId).first<{ frozen_at: string | null; live: number }>();
        if (!human) return { ok: false as const, reason: 'not-found' as const };
        if (human.frozen_at !== null) return { ok: false as const, reason: 'frozen' as const };
        // Counted in the same optimistic transaction as the insert, so concurrent consents cannot pass the limit.
        if (human.live >= maxLiveOwnerGrants) return { ok: false as const, reason: 'grant-limit' as const };
        tx.writes.push(tx.db.prepare('INSERT INTO owner_grants(grant_id,human_id,client_id,client_name,created_at,revoked_at) VALUES(?,?,?,?,CAST(? AS INTEGER),NULL)')
            .bind(input.grantId, input.humanId, input.clientId, clientName, tx.store.now.toString()));
        if (input.appDecides === true)
            tx.writes.push(tx.db.prepare('INSERT INTO owner_grant_approvals(grant_id,human_id,created_at,revoked_at) VALUES(?,?,CAST(? AS INTEGER),NULL)')
                .bind(input.grantId, input.humanId, tx.store.now.toString()));
        return { ok: true as const };
    }, clock);
}

/** Revokes one of the human's grants. Idempotent: an already revoked grant is ok and keeps its first revocation time. */
export async function revokeGrant(env: Env, input: RevokeGrantInput, clock: () => bigint = nowUtc): Promise<RevokeGrantResult> {
    if (writesRefused(env)) return { ok: false, reason: 'unavailable' };
    if (!uuid(input?.humanId)) fail();
    if (!uuid(input.grantId)) return { ok: false, reason: 'not-found' };
    return atomic(env.db, tx => revokeGrantIn(tx, input.humanId, input.grantId), clock);
}

/** The revocation inside the caller's transaction (the owner action decides under one human re-read). */
export async function revokeGrantIn(tx: Transaction, humanId: string, grantId: string): Promise<RevokeGrantResult> {
    // An agent's grant or an owner grant: the same revocation, in whichever table holds the ID.
    for (const table of ['client_grants', 'owner_grants'] as const) {
        const grant = await tx.db.prepare(`SELECT human_id,CAST(revoked_at AS TEXT) revoked_at FROM ${table} WHERE grant_id=?`)
            .bind(grantId).first<{ human_id: string; revoked_at: string | null }>();
        if (!grant) continue;
        // Another human's grant is indistinguishable from an absent one.
        if (grant.human_id !== humanId) return { ok: false, reason: 'not-found' };
        if (grant.revoked_at === null)
            tx.writes.push(tx.db.prepare(`UPDATE ${table} SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=? AND revoked_at IS NULL`)
                .bind(tx.store.now.toString(), grantId));
        // Revoking an owner grant also ends its permission to decide, so the record never shows a live permission on a dead grant.
        if (table === 'owner_grants') tx.writes.push(stopDecisionsWrite(tx, grantId));
        return { ok: true };
    }
    return { ok: false, reason: 'not-found' };
}

const stopDecisionsWrite = (tx: Transaction, grantId: string) =>
    tx.db.prepare('UPDATE owner_grant_approvals SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=? AND revoked_at IS NULL').bind(tx.store.now.toString(), grantId);

/** Ends the owner grant's permission to approve or reject (A1), keeping the grant itself, inside the caller's transaction.
 * Idempotent; a grant of another human, or one that never had the permission, is not-found. The row is kept: `revoked_at`
 * records the end, so every decision keeps the permission that allowed it. */
export async function stopAppDecisionsIn(tx: Transaction, humanId: string, grantId: string): Promise<RevokeGrantResult> {
    const row = await tx.db.prepare('SELECT human_id FROM owner_grant_approvals WHERE grant_id=?').bind(grantId).first<{ human_id: string }>();
    if (!row || row.human_id !== humanId) return { ok: false, reason: 'not-found' };
    tx.writes.push(stopDecisionsWrite(tx, grantId));
    return { ok: true };
}

/** The live grant only while it is not revoked, its human still owns the agent and is not frozen; null otherwise.
 * Authorization reads it on every private call, and every write reads it again inside its own transaction, where the
 * revision check makes it current at commit. Freezing the human or moving the agent to another owner therefore ends
 * every token of the grant at once, including a write already authorized. */
export async function liveOwnedGrant(db: Database, grantId: string): Promise<GrantRow | null> {
    const row = await db.prepare('SELECT g.human_id,g.account_id,g.client_id FROM client_grants g'
        + ' JOIN accounts a ON a.account_id=g.account_id AND a.human_id=g.human_id'
        + ' JOIN humans h ON h.human_id=g.human_id AND h.frozen_at IS NULL'
        + ' WHERE g.grant_id=? AND g.revoked_at IS NULL')
        .bind(grantId).first<{ human_id: string; account_id: string; client_id: string }>();
    return row && { grantId, humanId: row.human_id, accountId: row.account_id, clientId: row.client_id };
}

/** The live owner grant only while it is not revoked and its human is not frozen; null otherwise. */
export async function liveOwnerGrant(db: Database, grantId: string): Promise<OwnerGrantRow | null> {
    const row = await db.prepare('SELECT g.human_id,g.client_id FROM owner_grants g JOIN humans h ON h.human_id=g.human_id AND h.frozen_at IS NULL'
        + ' WHERE g.grant_id=? AND g.revoked_at IS NULL').bind(grantId).first<{ human_id: string; client_id: string }>();
    return row && { grantId, humanId: row.human_id, clientId: row.client_id };
}

/** Whether the grant may act as the agent now: an agent's own live grant, or a live owner grant of the human who owns the
 * agent at this moment (so an agent bound to that human later is covered, and one that left them is not). */
export async function grantActsAs(db: Database, grantId: string, accountId: string): Promise<boolean> {
    if (!uuid(grantId) || !uuid(accountId)) return false;
    const agent = await liveOwnedGrant(db, grantId);
    if (agent) return agent.accountId === accountId;
    return await db.prepare('SELECT 1 n FROM owner_grants g JOIN humans h ON h.human_id=g.human_id AND h.frozen_at IS NULL'
        + ' JOIN accounts a ON a.human_id=g.human_id WHERE g.grant_id=? AND g.revoked_at IS NULL AND a.account_id=?').bind(grantId, accountId).first('n') !== null;
}

/** The live grant behind a token, of either kind: an agent's own grant names its agent; an owner grant names none and says,
 * per call, whether it may act as a given agent. */
export async function liveGrant(db: Database, grantId: string): Promise<LiveGrant | null> {
    const agent = await liveOwnedGrant(db, grantId);
    if (agent) return {accountId: agent.accountId, humanId: agent.humanId, clientId: agent.clientId};
    const owner = await liveOwnerGrant(db, grantId);
    return owner && {accountId: null, humanId: owner.humanId, clientId: owner.clientId, actsAs: agentId => grantActsAs(db, grantId, agentId)};
}

/** An external-action caller authorized by an OAuth consent grant: each check re-reads the grant inside the caller's own
 * transaction, so a revocation, an ownership change or a freeze committed first refuses the operation. */
export const grantCaller = (accountId: string, grantId: string) => ({accountId, check: async (tx: Transaction): Promise<void> => {
    // A missing grant identity never skips the check.
    if (typeof grantId !== 'string' || grantId === '' || !await grantActsAs(tx.db, grantId, accountId)) fail(401, 'grant-revoked');
}});

/** The same re-check for an owner grant acting on its human's behalf rather than as one agent. */
export const ownerGrantCheck = (humanId: string, grantId: string) => async (tx: Transaction): Promise<void> => {
    const grant = typeof grantId === 'string' && uuid(grantId) ? await liveOwnerGrant(tx.db, grantId) : null;
    if (!grant || grant.humanId !== humanId) fail(401, 'grant-revoked');
};
