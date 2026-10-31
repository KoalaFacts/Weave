import { atomic, type Env, type Transaction } from './database.ts';
import { writesRefused } from './humans.ts';
import { nowUtc } from './time.ts';
import { fail } from './wire.ts';

// The mailbox mirror of each OAuth consent grant. The authorization server records a grant before it issues an
// authorization code; every private /mcp call then requires the token's grant row to be live, so a revocation takes
// effect on the next call without waiting for issued access or refresh tokens to expire.

export type GrantRow = { grantId: string; humanId: string; accountId: string; clientId: string };
export type RecordGrantInput = GrantRow;
export type RecordGrantResult = { ok: true } | { ok: false; reason: 'not-found' | 'frozen' | 'unavailable' };
export type RevokeGrantInput = { humanId: string; grantId: string };
export type RevokeGrantResult = { ok: true } | { ok: false; reason: 'not-found' | 'unavailable' };

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const controlCharacters = /[\u0000-\u001f\u007f-\u009f]/u;
const uuid = (value: unknown): value is string => typeof value === 'string' && uuidPattern.test(value);

/** Records a grant for an agent the (not frozen) human owns. The authorization server issues no code unless this returns ok. */
export async function recordGrant(env: Env, input: RecordGrantInput, clock: () => bigint = nowUtc): Promise<RecordGrantResult> {
    if (writesRefused(env)) return { ok: false, reason: 'unavailable' };
    if (!uuid(input?.grantId) || !uuid(input.humanId) || !uuid(input.accountId) || typeof input.clientId !== 'string'
        || input.clientId.length < 1 || input.clientId.length > 2048 || controlCharacters.test(input.clientId)) fail();
    return atomic(env.db, async tx => {
        const human = await tx.db.prepare('SELECT frozen_at,(SELECT 1 FROM accounts WHERE account_id=? AND human_id=humans.human_id) owned FROM humans WHERE human_id=?')
            .bind(input.accountId, input.humanId).first<{ frozen_at: string | null; owned: number | null }>();
        if (!human) return { ok: false as const, reason: 'not-found' as const };
        if (human.frozen_at !== null) return { ok: false as const, reason: 'frozen' as const };
        if (human.owned === null) return { ok: false as const, reason: 'not-found' as const };
        tx.writes.push(tx.db.prepare('INSERT INTO client_grants(grant_id,human_id,account_id,client_id,created_at,revoked_at) VALUES(?,?,?,?,CAST(? AS INTEGER),NULL)')
            .bind(input.grantId, input.humanId, input.accountId, input.clientId, tx.store.now.toString()));
        return { ok: true as const };
    }, clock);
}

/** Revokes one of the human's grants. Idempotent: an already revoked grant is ok and keeps its first revocation time. */
export async function revokeGrant(env: Env, input: RevokeGrantInput, clock: () => bigint = nowUtc): Promise<RevokeGrantResult> {
    if (writesRefused(env)) return { ok: false, reason: 'unavailable' };
    if (!uuid(input?.humanId)) fail();
    if (!uuid(input.grantId)) return { ok: false, reason: 'not-found' };
    return atomic(env.db, async tx => {
        const grant = await tx.db.prepare('SELECT human_id,CAST(revoked_at AS TEXT) revoked_at FROM client_grants WHERE grant_id=?')
            .bind(input.grantId).first<{ human_id: string; revoked_at: string | null }>();
        // Another human's grant is indistinguishable from an absent one.
        if (!grant || grant.human_id !== input.humanId) return { ok: false as const, reason: 'not-found' as const };
        if (grant.revoked_at === null)
            tx.writes.push(tx.db.prepare('UPDATE client_grants SET revoked_at=CAST(? AS INTEGER) WHERE grant_id=? AND revoked_at IS NULL')
                .bind(tx.store.now.toString(), input.grantId));
        return { ok: true as const };
    }, clock);
}

/** The grant while it is not revoked; null when it is revoked or unknown. */
export async function liveGrant(tx: Transaction, grantId: string): Promise<GrantRow | null> {
    const row = await tx.db.prepare('SELECT human_id,account_id,client_id FROM client_grants WHERE grant_id=? AND revoked_at IS NULL')
        .bind(grantId).first<{ human_id: string; account_id: string; client_id: string }>();
    return row && { grantId, humanId: row.human_id, accountId: row.account_id, clientId: row.client_id };
}
