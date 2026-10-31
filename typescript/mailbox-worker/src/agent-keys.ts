import * as schema from './schema.ts';
import type { AgentAuth } from './agent-tokens.ts';
import { jwkThumbprint, parseJws, publicJwk, verifyJws, type CompactJws, type Ed25519Jwk } from './jose.ts';
import { apiTime, epochSeconds, exactInt64, nowUtc } from './time.ts';
import { atomic, type Database, type Env, type Transaction } from './database.ts';
import { pendingKeyRequest, stageKeyRequest, type KeyRequestView } from './key-requests.ts';
import { Problem, fail, invalidField, object } from './wire.ts';

export const maxLiveKeysPerAgent = 100;
export const keyCoolingTicks = 864000000000n; // 24 h

export type KeyRow = {kid: string; accountId: string; jwk: Ed25519Jwk; label: string | null; createdAt: bigint; lastUsedAt: bigint | null; revokedAt: bigint | null; coolingUntil: bigint | null};
export type AgentKeySummary = {kid: string; label: string | null; createdAt: string; lastUsedAt: string | null; coolingUntil: string | null};

type StoredKey = {kid: string; account_id: string; public_jwk: string; label: string | null; created_at: string; last_used_at: string | null; revoked_at: string | null; cooling_until: string | null};
const optionalTicks = (value: string | null) => value === null ? null : exactInt64(value);
const controlCharacters = /[\u0000-\u001f\u007f-\u009f]/u;

const keyColumns = `kid,account_id,public_jwk,label,CAST(created_at AS TEXT) created_at,CAST(last_used_at AS TEXT) last_used_at,
    CAST(revoked_at AS TEXT) revoked_at,CAST(cooling_until AS TEXT) cooling_until`;

export async function findKey(db: Database, kid: string): Promise<KeyRow | null> {
    const row = await db.prepare(`SELECT ${keyColumns} FROM agent_keys WHERE kid=?`).bind(kid).first<StoredKey>();
    return row ? keyRow(row) : null;
}

function keyRow(row: StoredKey): KeyRow {
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

const keyRequestSkewSeconds = 60;

export const isCooling = (row: KeyRow, now: bigint): boolean => row.coolingUntil !== null && row.coolingUntil > now;

export function conflict(field: string, reason: 'cooling' | 'key-in-use' | 'last-live-key'): never {
    throw new Problem(409, 'conflict', undefined, {field, reason});
}

/** Claims shared by the key proof and the endorsement: exact type, audience and agent, iat within ±60 s. */
function bindsKeyRequest(jws: CompactJws, type: string, audience: string, accountId: string, now: number): boolean {
    const {aud, sub, iat} = jws.payload;
    return jws.header.typ === type && aud === audience && sub === accountId && typeof iat === 'number' && Number.isSafeInteger(iat)
        && Math.abs(iat - now) <= keyRequestSkewSeconds;
}

/** GET /v1/keys: the agent's live keys, oldest first. */
export async function listAgentKeys(env: Env, accountId: string): Promise<{items: AgentKeySummary[]}> {
    const {results} = await env.db.prepare(`SELECT ${keyColumns} FROM agent_keys WHERE account_id=? AND revoked_at IS NULL ORDER BY created_at,kid`)
        .bind(accountId).all<StoredKey>();
    return {items: results.map(row => keySummary(keyRow(row)))};
}

/**
 * POST /v1/keys: the new key proves possession with a self-signed proof and the authenticated key endorses its thumbprint,
 * because a DPoP proof covers neither the body nor replay. On an ownerless agent the added key cools for 24 h; on a human-owned
 * agent it is held as a pending key request (202) until the owner confirms it, and cooling does not apply.
 */
export async function addAgentKey(env: Env, request: Request, auth: AgentAuth, input: unknown): Promise<{status: 200 | 202; body: AgentKeySummary | KeyRequestView}> {
    object(input, ['proof', 'endorsement'], ['label']);
    const {proof: proofText, endorsement: endorsementText, label: labelValue} = input as {proof: unknown; endorsement: unknown; label?: unknown};
    const label = validLabel(labelValue);
    const audience = new URL(request.url).origin + '/v1/keys';
    const now = epochSeconds(nowUtc());

    const proof = (typeof proofText === 'string' ? parseJws(proofText) : null) ?? invalidField('proof', 'must be a compact EdDSA JWS');
    // publicJwk refuses private (`d`) and unknown members; the refused value is never stored or echoed.
    const jwk = publicJwk(proof.header.jwk) ?? invalidField('proof', 'jwk must be a public Ed25519 key');
    if (!bindsKeyRequest(proof, 'weave-key-proof+jwt', audience, auth.accountId, now) || !await verifyJws(proof, jwk))
        invalidField('proof', 'must be a key proof for this agent signed by the new key');
    const kid = await jwkThumbprint(jwk);

    const endorsement = (typeof endorsementText === 'string' ? parseJws(endorsementText) : null) ?? invalidField('endorsement', 'must be a compact EdDSA JWS');
    if (endorsement.header.kid !== auth.kid || endorsement.payload.newKid !== kid
        || !bindsKeyRequest(endorsement, 'weave-key-endorsement+jwt', audience, auth.accountId, now)) invalidField('endorsement', 'must endorse the new key with the authenticated key');
    const endorser = await findKey(env.db, auth.kid);
    if (!endorser || endorser.revokedAt !== null || endorser.accountId !== auth.accountId) fail(401, 'unauthenticated');
    if (!await verifyJws(endorsement, endorser.jwk)) invalidField('endorsement', 'must endorse the new key with the authenticated key');

    return atomic(env.db, async tx => {
        const at = tx.store.now;
        const owner = await tx.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(auth.accountId).first<{human_id: string | null}>();
        if (!owner) fail(401, 'unauthenticated');
        const owned = owner.human_id !== null;
        const current = await findKey(tx.db, auth.kid);
        if (!current || current.revokedAt !== null || current.accountId !== auth.accountId) fail(401, 'unauthenticated');
        if (!owned && isCooling(current, at)) conflict('endorsement', 'cooling');
        const existing = await findKey(tx.db, kid);
        if (existing) {
            if (existing.accountId === auth.accountId && existing.revokedAt === null) return {status: 200, body: keySummary(existing)};
            conflict('proof', 'key-in-use');
        }
        // A retry of a pending owned-agent addition is answered before any limit, so it stays idempotent.
        const pending = owned ? await pendingKeyRequest(tx.db, auth.accountId, kid, at) : null;
        if (pending) return {status: 202, body: pending};
        // Counted inside the same optimistic transaction as the insert, so concurrent adds cannot pass the limit.
        if (await countLiveKeys(tx, auth.accountId) >= maxLiveKeysPerAgent) fail(429, 'capacity');
        if (owned) return {status: 202, body: await stageKeyRequest(tx, {accountId: auth.accountId, kid, jwk, label, endorsedBy: auth.kid})};
        const coolingUntil = at + keyCoolingTicks;
        insertKey(tx, {kid, accountId: auth.accountId, jwk, label, coolingUntil});
        return {status: 200, body: keySummary({kid, accountId: auth.accountId, jwk, label, createdAt: at, lastUsedAt: null, revokedAt: null, coolingUntil})};
    });
}

/**
 * DELETE /v1/keys/{kid}. While any key of an ownerless agent cools, its settled keys must survive so that they can still revoke
 * the cooling key: no settled key is revoked (not even by itself) and a cooling key may revoke only itself. The last live key
 * of an ownerless agent is never revoked.
 */
export async function revokeAgentKey(env: Env, auth: AgentAuth, kid: string, clock: () => bigint = nowUtc): Promise<{kid: string; revokedAt: string}> {
    return atomic(env.db, async tx => {
        const at = clock();
        const target = await findKey(tx.db, kid);
        if (!target || target.accountId !== auth.accountId) fail(404, 'unavailable');
        if (target.revokedAt !== null) return {kid, revokedAt: apiTime(target.revokedAt)!};
        const caller = target.kid === auth.kid ? target : await findKey(tx.db, auth.kid);
        if (!caller || caller.accountId !== auth.accountId) fail(401, 'unauthenticated');
        const owner = await tx.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(auth.accountId).first<{human_id: string | null}>();
        if (!owner) fail(401, 'unauthenticated');
        if (owner.human_id === null) {
            if (caller !== target && isCooling(caller, at)) conflict('kid', 'cooling');
            if (!isCooling(target, at)) {
                const cooling = await tx.db.prepare('SELECT count(*) n FROM agent_keys WHERE account_id=? AND revoked_at IS NULL AND cooling_until>CAST(? AS INTEGER)')
                    .bind(auth.accountId, at.toString()).first<number>('n');
                if (cooling === null) fail(503, 'storage');
                if (cooling > 0) conflict('kid', 'cooling');
            }
            if (await countLiveKeys(tx, auth.accountId) <= 1) conflict('kid', 'last-live-key');
        }
        // After the last-key rule: two keys revoking each other concurrently end with one live key and a 409, not a 401.
        if (caller.revokedAt !== null) fail(401, 'unauthenticated');
        const stamp = at.toString();
        tx.writes.push(tx.db.prepare('UPDATE agent_keys SET revoked_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER) WHERE kid=? AND revoked_at IS NULL').bind(stamp, stamp, kid));
        return {kid, revokedAt: apiTime(at)!};
    });
}
