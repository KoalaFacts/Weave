import * as schema from './schema.ts';
import { countLiveKeys, findKey, validLabel } from './agent-keys.ts';
import { prunedRowsPerCreate, requestLifetimeTicks, retentionTicks } from './binding-requests.ts';
import { verifyDpop } from './dpop.ts';
import { publicJwk, type Ed25519Jwk } from './jose.ts';
import { apiTime, epochSeconds, exactInt64, nowUtc } from './time.ts';
import { atomic, type Database, type Env, type Transaction } from './database.ts';
import { Problem, fail, invalidField, object } from './wire.ts';

export const pendingKeyRequestsPerAgent = 3;

export type KeyRequestStatus = 'pending' | 'confirmed' | 'rejected' | 'expired';
export type KeyRequestView = {requestId: string; agentId: string; kid: string; label: string | null; endorsedBy: string | null; status: KeyRequestStatus;
    createdAt: string; expiresAt: string; decidedAt: string | null};
export type KeyRequestRow = {requestId: string; accountId: string; kid: string; jwk: Ed25519Jwk; label: string | null; endorsedBy: string | null;
    status: KeyRequestStatus; createdAt: bigint; expiresAt: bigint; decidedAt: bigint | null};

type StoredKeyRequest = {request_id: string; account_id: string; kid: string; public_jwk: string; label: string | null; endorsed_by: string | null; status: string;
    created_at: string; expires_at: string; decided_at: string | null};

const statuses = new Set<string>(['pending', 'confirmed', 'rejected', 'expired']);
const keyRequestColumns = `request_id,account_id,kid,public_jwk,label,endorsed_by,status,CAST(created_at AS TEXT) created_at,CAST(expires_at AS TEXT) expires_at,
    CAST(decided_at AS TEXT) decided_at`;

function keyRequestRow(row: StoredKeyRequest): KeyRequestRow {
    // Only the four statuses and canonical public JWKs are written; anything else is storage corruption.
    if (!statuses.has(row.status)) fail(503, 'storage');
    let stored: unknown;
    try { stored = JSON.parse(row.public_jwk); } catch { fail(503, 'storage'); }
    const jwk = publicJwk(stored) ?? fail(503, 'storage');
    return {requestId: row.request_id, accountId: row.account_id, kid: row.kid, jwk, label: row.label, endorsedBy: row.endorsed_by, status: row.status as KeyRequestStatus,
        createdAt: exactInt64(row.created_at), expiresAt: exactInt64(row.expires_at), decidedAt: row.decided_at === null ? null : exactInt64(row.decided_at)};
}

export async function readKeyRequest(db: Database, requestId: string): Promise<KeyRequestRow | null> {
    const row = await db.prepare(`SELECT ${keyRequestColumns} FROM key_requests WHERE request_id=?`).bind(requestId).first<StoredKeyRequest>();
    return row ? keyRequestRow(row) : null;
}

/** A pending row past its expiry reads as expired even before any write records it. */
export function keyRequestView(row: KeyRequestRow, now: bigint): KeyRequestView {
    const status = row.status === 'pending' && row.expiresAt <= now ? 'expired' : row.status;
    return {requestId: row.requestId, agentId: row.accountId, kid: row.kid, label: row.label, endorsedBy: row.endorsedBy, status,
        createdAt: apiTime(row.createdAt)!, expiresAt: apiTime(row.expiresAt)!, decidedAt: apiTime(row.decidedAt)};
}

/**
 * Stages a pending key request in the caller's transaction, at the transaction time. A retry for the same kid returns the live
 * pending request; the pending count is read in the same optimistic transaction as the insert.
 */
export async function stageKeyRequest(tx: Transaction, input: {accountId: string; kid: string; jwk: Ed25519Jwk; label: string | null; endorsedBy: string | null}): Promise<KeyRequestView> {
    const at = tx.store.now, stamp = at.toString();
    const existing = await tx.db.prepare(`SELECT ${keyRequestColumns} FROM key_requests WHERE account_id=? AND kid=? AND status='pending' AND expires_at>CAST(? AS INTEGER)`)
        .bind(input.accountId, input.kid, stamp).first<StoredKeyRequest>();
    if (existing) return keyRequestView(keyRequestRow(existing), at);
    // Staged before the INSERT, so a stale pending row for the same kid no longer holds key_requests_pending.
    tx.writes.push(tx.db.prepare(`UPDATE key_requests SET status='expired',decided_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER)
        WHERE account_id=? AND status='pending' AND expires_at<=CAST(? AS INTEGER)`).bind(stamp, stamp, input.accountId, stamp));
    const pending = await tx.db.prepare(`SELECT count(*) n FROM key_requests WHERE account_id=? AND status='pending' AND expires_at>CAST(? AS INTEGER)`)
        .bind(input.accountId, stamp).first<number>('n');
    if (pending === null) fail(503, 'storage');
    if (pending >= pendingKeyRequestsPerAgent) fail(429, 'capacity');
    // Bounded retention cleanup of decided rows through key_requests_retention; pending rows are only ever expired.
    tx.writes.push(tx.db.prepare(`DELETE FROM key_requests WHERE request_id IN (SELECT request_id FROM key_requests
        WHERE status IN ('confirmed','rejected','expired') AND created_at<=CAST(? AS INTEGER) LIMIT ?)`).bind((at - retentionTicks).toString(), prunedRowsPerCreate));
    const row: KeyRequestRow = {requestId: crypto.randomUUID(), accountId: input.accountId, kid: input.kid, jwk: input.jwk, label: input.label, endorsedBy: input.endorsedBy,
        status: 'pending', createdAt: at, expiresAt: at + requestLifetimeTicks, decidedAt: null};
    tx.store.stage(tx.store.orm.insert(schema.keyRequests).values({
        request_id: row.requestId, account_id: row.accountId, kid: row.kid, public_jwk: JSON.stringify({crv: row.jwk.crv, kty: row.jwk.kty, x: row.jwk.x}),
        label: row.label, endorsed_by: row.endorsedBy, status: row.status, created_at: row.createdAt, expires_at: row.expiresAt, updated_at: at, decided_at: null,
    }));
    return keyRequestView(row, at);
}

/**
 * POST /v1/key-requests: owner recovery for a human-owned agent with no live key. The DPoP proof key is the requested key.
 * Unknown, ownerless and keyed agents get the same 404, so the route is no ownership oracle; those checks precede key-in-use.
 */
export async function requestRecoveryKey(env: Env, request: Request, input: unknown, clock: () => bigint = nowUtc): Promise<{status: 200 | 202; view: KeyRequestView}> {
    if (request.headers.has('Authorization')) fail();
    // No access token: a proof carrying `ath` is refused. publicJwk refuses private (`d`) members; nothing is stored or echoed.
    const proof = await verifyDpop(request, {now: epochSeconds(clock())});
    if (!proof.ok) fail(401, 'unauthenticated');
    object(input, ['agentId'], ['label']);
    const {agentId, label: labelValue} = input as {agentId: unknown; label?: unknown};
    if (typeof agentId !== 'string') invalidField('agentId', 'must be an agent ID');
    const label = validLabel(labelValue);
    return atomic(env.db, async tx => {
        const at = tx.store.now;
        const owner = await tx.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(agentId).first<{human_id: string | null}>();
        if (!owner || owner.human_id === null) fail(404, 'unavailable');
        const existing = await findKey(tx.db, proof.jkt);
        if (existing && existing.accountId === agentId && existing.revokedAt === null) {
            // A retry after the owner confirmed: only the key's holder can ask, and it already knows the key is on this agent.
            const confirmed = await tx.db.prepare(`SELECT ${keyRequestColumns} FROM key_requests WHERE account_id=? AND kid=? AND status='confirmed'
                ORDER BY decided_at DESC LIMIT 1`).bind(agentId, proof.jkt).first<StoredKeyRequest>();
            if (confirmed) return {status: 200, view: keyRequestView(keyRequestRow(confirmed), at)};
        }
        if (await countLiveKeys(tx, agentId) > 0) fail(404, 'unavailable');
        if (existing) throw new Problem(409, 'conflict', undefined, {field: 'proof', reason: 'key-in-use'});
        return {status: 202, view: await stageKeyRequest(tx, {accountId: agentId, kid: proof.jkt, jwk: proof.jwk, label, endorsedBy: null})};
    });
}
