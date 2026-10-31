import * as schema from './schema.ts';
import { publicJwk, type Ed25519Jwk } from './jose.ts';
import { apiTime, exactInt64 } from './time.ts';
import type { Database, Transaction } from './database.ts';
import { fail, invalidField } from './wire.ts';

export const maxLiveKeysPerAgent = 100;
export const keyCoolingTicks = 864000000000n; // 24 h

export type KeyRow = {kid: string; accountId: string; jwk: Ed25519Jwk; label: string | null; createdAt: bigint; lastUsedAt: bigint | null; revokedAt: bigint | null; coolingUntil: bigint | null};
export type AgentKeySummary = {kid: string; label: string | null; createdAt: string; lastUsedAt: string | null; coolingUntil: string | null};

type StoredKey = {kid: string; account_id: string; public_jwk: string; label: string | null; created_at: string; last_used_at: string | null; revoked_at: string | null; cooling_until: string | null};
const optionalTicks = (value: string | null) => value === null ? null : exactInt64(value);
const controlCharacters = /[\u0000-\u001f\u007f-\u009f]/u;

export async function findKey(db: Database, kid: string): Promise<KeyRow | null> {
    const row = await db.prepare(`SELECT kid,account_id,public_jwk,label,CAST(created_at AS TEXT) created_at,CAST(last_used_at AS TEXT) last_used_at,
        CAST(revoked_at AS TEXT) revoked_at,CAST(cooling_until AS TEXT) cooling_until FROM agent_keys WHERE kid=?`).bind(kid).first<StoredKey>();
    if (!row) return null;
    // Only canonical public JWKs are written; anything else is storage corruption, not a usable key.
    let stored: unknown;
    try { stored = JSON.parse(row.public_jwk); } catch { fail(503, 'storage'); }
    const jwk = publicJwk(stored) ?? fail(503, 'storage');
    return {kid: row.kid, accountId: row.account_id, jwk, label: row.label, createdAt: exactInt64(row.created_at),
        lastUsedAt: optionalTicks(row.last_used_at), revokedAt: optionalTicks(row.revoked_at), coolingUntil: optionalTicks(row.cooling_until)};
}

/** Stages the INSERT in the caller's transaction; the key is created at the transaction time. */
export function insertKey(tx: Transaction, row: {kid: string; accountId: string; jwk: Ed25519Jwk; label: string | null; coolingUntil: bigint | null}): void {
    const now = tx.store.now;
    tx.store.stage(tx.store.orm.insert(schema.agentKeys).values({
        kid: row.kid, account_id: row.accountId, public_jwk: JSON.stringify({crv: row.jwk.crv, kty: row.jwk.kty, x: row.jwk.x}),
        label: row.label, created_at: now, cooling_until: row.coolingUntil, updated_at: now,
    }));
}

export async function countLiveKeys(tx: Transaction, accountId: string): Promise<number> {
    const n = await tx.db.prepare('SELECT count(*) n FROM agent_keys WHERE account_id=? AND revoked_at IS NULL').bind(accountId).first<number>('n');
    return n ?? fail(503, 'storage');
}

export function keySummary(row: KeyRow): AgentKeySummary {
    return {kid: row.kid, label: row.label, createdAt: apiTime(row.createdAt)!, lastUsedAt: apiTime(row.lastUsedAt), coolingUntil: apiTime(row.coolingUntil)};
}

export function validLabel(value: unknown): string | null {
    if (value === undefined || value === null) return null;
    if (typeof value !== 'string' || value.length < 1 || value.length > 64 || controlCharacters.test(value))
        invalidField('label', 'must be 1 to 64 characters without control characters');
    return value;
}
