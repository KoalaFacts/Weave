import * as schema from './schema.ts';
import type { AgentAuth } from './agent-tokens.ts';
import { findKey, isCooling } from './agent-keys.ts';
import { base64url, parseJws, verifyJws } from './jose.ts';
import { apiTime, epochSeconds, exactInt64, nowUtc } from './time.ts';
import { atomic, type Database, type Env, type Transaction } from './database.ts';
import { maxAgentsPerHuman } from './humans.ts';
import type { OwnerDecision, OwnerHuman } from './owner-actions.ts';
import { prunedRowsPerCreate, requestLifetimeTicks, retentionTicks } from './request-retention.ts';
import { Problem, fail, invalidField, object } from './wire.ts';

export const bindingRequestsPerAgentPerDay = 5;
export const pendingBindingRequestsPerHuman = 20;

export type BindingStatus = 'pending' | 'confirmed' | 'rejected' | 'cancelled' | 'expired';
export type BindingRequestView = {requestId: string; agentId: string; human: string; kid: string; status: BindingStatus; createdAt: string; expiresAt: string; decidedAt: string | null};
export type BindingRow = {requestId: string; accountId: string; targetSubject: string; kid: string; status: BindingStatus; createdAt: bigint; expiresAt: bigint; decidedAt: bigint | null};

export type StoredBinding = {request_id: string; account_id: string; target_subject: string; kid: string; status: string; created_at: string; expires_at: string; decided_at: string | null};

const statuses = new Set<string>(['pending', 'confirmed', 'rejected', 'cancelled', 'expired']);
const humanTarget = /^github:([1-9][0-9]{0,19})$/;
const skewSeconds = 60;
export const bindingColumns = `request_id,account_id,target_subject,kid,status,CAST(created_at AS TEXT) created_at,CAST(expires_at AS TEXT) expires_at,
    CAST(decided_at AS TEXT) decided_at`;

export function bindingRow(row: StoredBinding): BindingRow {
    // Only the five statuses are written; anything else is storage corruption.
    if (!statuses.has(row.status)) fail(503, 'storage');
    return {requestId: row.request_id, accountId: row.account_id, targetSubject: row.target_subject, kid: row.kid, status: row.status as BindingStatus,
        createdAt: exactInt64(row.created_at), expiresAt: exactInt64(row.expires_at), decidedAt: row.decided_at === null ? null : exactInt64(row.decided_at)};
}

export async function readBindingRow(db: Database, requestId: string): Promise<BindingRow | null> {
    const row = await db.prepare(`SELECT ${bindingColumns} FROM binding_requests WHERE request_id=?`).bind(requestId).first<StoredBinding>();
    return row ? bindingRow(row) : null;
}

/** A pending row past its expiry reads as expired even before any write records it. */
export function bindingView(row: BindingRow, now: bigint): BindingRequestView {
    const status = row.status === 'pending' && row.expiresAt <= now ? 'expired' : row.status;
    return {requestId: row.requestId, agentId: row.accountId, human: 'github:' + row.targetSubject, kid: row.kid, status,
        createdAt: apiTime(row.createdAt)!, expiresAt: apiTime(row.expiresAt)!, decidedAt: apiTime(row.decidedAt)};
}

/**
 * What the owner page shows and the confirmation re-checks: the SHA-256 (base64url) of the agent's live key IDs, sorted and joined by
 * newlines, so a key added or revoked after the page was rendered refuses the confirmation instead of being adopted unseen.
 */
export async function liveKeysDigest(kids: string[]): Promise<string> {
    const text = [...kids].sort().join('\n');
    return base64url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text))));
}

function conflict(reason: 'owned-agent' | 'cooling' | 'binding-pending'): never {
    throw new Problem(409, 'conflict', undefined, {field: 'request', reason});
}
function invalidRequest(): never { invalidField('request', 'must be a binding request for this agent signed by the authenticated key'); }

/** The JWS fixes the target human, because the DPoP proof covers neither the body nor replay. Returns the GitHub numeric subject. */
async function signedTarget(env: Env, request: Request, auth: AgentAuth, text: unknown, now: number): Promise<string> {
    const jws = (typeof text === 'string' ? parseJws(text) : null) ?? invalidRequest();
    const {aud, sub, iat, human} = jws.payload;
    if (jws.header.typ !== 'weave-binding-request+jwt' || jws.header.kid !== auth.kid || aud !== new URL(request.url).origin + '/v1/binding-requests'
        || sub !== auth.accountId || typeof iat !== 'number' || !Number.isSafeInteger(iat) || Math.abs(iat - now) > skewSeconds) invalidRequest();
    const subject = (typeof human === 'string' ? humanTarget.exec(human)?.[1] : undefined) ?? invalidRequest();
    const key = await findKey(env.db, auth.kid);
    if (!key || key.revokedAt !== null || key.accountId !== auth.accountId) fail(401, 'unauthenticated');
    if (!await verifyJws(jws, key.jwk)) invalidRequest();
    return subject;
}

/**
 * POST /v1/binding-requests. Whether the named human exists, is frozen or active is never consulted, so every accepted
 * request has the same shape. A retry naming the same human returns the pending request.
 */
export async function createBindingRequest(env: Env, request: Request, auth: AgentAuth, input: unknown, clock: () => bigint = nowUtc): Promise<BindingRequestView> {
    object(input, ['request']);
    const subject = await signedTarget(env, request, auth, (input as {request: unknown}).request, epochSeconds(clock()));
    return atomic(env.db, async tx => {
        const at = clock(), stamp = at.toString();
        const owner = await tx.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(auth.accountId).first<{human_id: string | null}>();
        if (!owner) fail(401, 'unauthenticated');
        if (owner.human_id !== null) conflict('owned-agent');
        const caller = await findKey(tx.db, auth.kid);
        if (!caller || caller.revokedAt !== null || caller.accountId !== auth.accountId) fail(401, 'unauthenticated');
        if (isCooling(caller, at)) conflict('cooling');

        // binding_requests_pending allows at most one pending row per agent.
        const pending = await tx.db.prepare(`SELECT ${bindingColumns} FROM binding_requests WHERE account_id=? AND status='pending'`).bind(auth.accountId).first<StoredBinding>();
        if (pending) {
            const row = bindingRow(pending);
            if (row.expiresAt > at) {
                if (row.targetSubject === subject) return bindingView(row, at);
                conflict('binding-pending');
            }
            tx.writes.push(tx.db.prepare(`UPDATE binding_requests SET status='expired',decided_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER)
                WHERE account_id=? AND status='pending' AND expires_at<=CAST(? AS INTEGER)`).bind(stamp, stamp, auth.accountId, stamp));
        }
        // Both limits are counted inside the same optimistic transaction as the insert, so concurrent requests cannot pass them.
        const recent = await tx.db.prepare('SELECT count(*) n FROM binding_requests WHERE account_id=? AND created_at>CAST(? AS INTEGER)')
            .bind(auth.accountId, (at - requestLifetimeTicks).toString()).first<number>('n');
        if (recent === null) fail(503, 'storage');
        if (recent >= bindingRequestsPerAgentPerDay) fail(429, 'capacity');
        const waiting = await tx.db.prepare(`SELECT count(*) n FROM binding_requests WHERE target_subject=? AND status='pending' AND expires_at>CAST(? AS INTEGER)`)
            .bind(subject, stamp).first<number>('n');
        if (waiting === null) fail(503, 'storage');
        if (waiting >= pendingBindingRequestsPerHuman) fail(429, 'capacity');

        // Bounded retention cleanup of refused rows through binding_requests_retention. Pending rows are only ever expired, and
        // confirmed rows are kept: they are the consent record behind accounts.human_id.
        tx.writes.push(tx.db.prepare(`DELETE FROM binding_requests WHERE request_id IN (SELECT request_id FROM binding_requests
            WHERE status IN ('rejected','cancelled','expired') AND created_at<=CAST(? AS INTEGER) LIMIT ?)`).bind((at - retentionTicks).toString(), prunedRowsPerCreate));
        const row: BindingRow = {requestId: crypto.randomUUID(), accountId: auth.accountId, targetSubject: subject, kid: auth.kid, status: 'pending',
            createdAt: at, expiresAt: at + requestLifetimeTicks, decidedAt: null};
        tx.store.stage(tx.store.orm.insert(schema.bindingRequests).values({
            request_id: row.requestId, account_id: row.accountId, target_subject: row.targetSubject, kid: row.kid, status: row.status,
            created_at: row.createdAt, expires_at: row.expiresAt, updated_at: at, decided_at: null,
        }));
        return bindingView(row, at);
    });
}

/** GET /v1/binding-requests/{requestId}: another agent's request is indistinguishable from a missing one. */
export async function getBindingRequest(env: Env, auth: AgentAuth, requestId: string, clock: () => bigint = nowUtc): Promise<BindingRequestView> {
    const row = await readBindingRow(env.db, requestId);
    if (!row || row.accountId !== auth.accountId) fail(404, 'unavailable');
    return bindingView(row, clock());
}

/** DELETE /v1/binding-requests/{requestId}: cancels a live pending request; any other state is returned unchanged. */
export async function cancelBindingRequest(env: Env, auth: AgentAuth, requestId: string, clock: () => bigint = nowUtc): Promise<BindingRequestView> {
    return atomic(env.db, async tx => {
        const at = clock(), stamp = at.toString();
        const row = await readBindingRow(tx.db, requestId);
        if (!row || row.accountId !== auth.accountId) fail(404, 'unavailable');
        if (row.status !== 'pending' || row.expiresAt <= at) return bindingView(row, at);
        const caller = await findKey(tx.db, auth.kid);
        if (!caller || caller.revokedAt !== null || caller.accountId !== auth.accountId) fail(401, 'unauthenticated');
        if (isCooling(caller, at)) conflict('cooling');
        tx.writes.push(tx.db.prepare(`UPDATE binding_requests SET status='cancelled',decided_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER)
            WHERE request_id=? AND status='pending'`).bind(stamp, stamp, requestId));
        return bindingView({...row, status: 'cancelled', decidedAt: at}, at);
    });
}

/** The request naming this human; another human's request is indistinguishable from a missing one. */
async function requestForHuman(tx: Transaction, human: OwnerHuman, requestId: string): Promise<BindingRow | null> {
    const row = await readBindingRow(tx.db, requestId);
    return row && row.targetSubject === human.subject ? row : null;
}

/**
 * Human-side confirmation, staged in the caller's transaction (the human row was re-read there). The guarded UPDATE keeps the
 * binding one-way, and the agent count is read in the same optimistic transaction, so concurrent confirmations cannot pass the limit.
 * `keysDigest` is the live key set the owner was shown; any other set now is `keys-changed`, so no key is adopted unseen.
 */
export async function confirmBindingRequest(tx: Transaction, human: OwnerHuman, requestId: string, keysDigest: string, at: bigint): Promise<OwnerDecision> {
    const row = await requestForHuman(tx, human, requestId);
    if (!row) return {ok: false, reason: 'not-found'};
    if (row.status !== 'pending') return {ok: false, reason: 'not-pending'};
    if (row.expiresAt <= at) return {ok: false, reason: 'expired'};
    const owner = await tx.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(row.accountId).first<{human_id: string | null}>();
    if (!owner) return {ok: false, reason: 'not-found'};
    if (owner.human_id !== null) return {ok: false, reason: 'already-owned'};
    const requesting = await findKey(tx.db, row.kid);
    if (!requesting || requesting.accountId !== row.accountId || requesting.revokedAt !== null) return {ok: false, reason: 'requesting-key-revoked'};
    const live = await tx.db.prepare('SELECT kid FROM agent_keys WHERE account_id=? AND revoked_at IS NULL').bind(row.accountId).all<{kid: string}>();
    if (await liveKeysDigest(live.results.map(key => key.kid)) !== keysDigest) return {ok: false, reason: 'keys-changed'};
    const owned = await tx.db.prepare('SELECT count(*) n FROM accounts WHERE human_id=?').bind(human.humanId).first<number>('n');
    if (owned === null) fail(503, 'storage');
    if (owned >= maxAgentsPerHuman) return {ok: false, reason: 'agent-limit'};
    const stamp = at.toString();
    tx.writes.push(
        tx.db.prepare('UPDATE accounts SET human_id=?,updated_at=CAST(? AS INTEGER) WHERE account_id=? AND human_id IS NULL').bind(human.humanId, stamp, row.accountId),
        tx.db.prepare(`UPDATE binding_requests SET status='confirmed',decided_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER)
            WHERE request_id=? AND status='pending'`).bind(stamp, stamp, requestId));
    return {ok: true};
}

/** A pending request is rejected even after its expiry; rejecting needs no fresh authentication. */
export async function rejectBindingRequest(tx: Transaction, human: OwnerHuman, requestId: string, at: bigint): Promise<OwnerDecision> {
    const row = await requestForHuman(tx, human, requestId);
    if (!row) return {ok: false, reason: 'not-found'};
    if (row.status !== 'pending') return {ok: false, reason: 'not-pending'};
    const stamp = at.toString();
    tx.writes.push(tx.db.prepare(`UPDATE binding_requests SET status='rejected',decided_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER)
        WHERE request_id=? AND status='pending'`).bind(stamp, stamp, requestId));
    return {ok: true};
}
