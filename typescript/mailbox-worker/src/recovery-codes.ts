import { conflict, countLiveKeys, findKey, insertKey, keySummary, maxLiveKeysPerAgent, validLabel, type AgentKeySummary } from './agent-keys.ts';
import { verifyDpop } from './dpop.ts';
import { base64url } from './jose.ts';
import { apiTime, epochSeconds, exactInt64, nowUtc } from './time.ts';
import { atomic, type Env } from './database.ts';
import { writesRefused } from './humans.ts';
import { fail, hash, invalidField, object } from './wire.ts';

export const recoveryCodeTicks = 6000000000n; // 10 min
export const recoveryAttemptsPerSourcePerHour = 20;

export type IssueRecoveryCodeResult = {ok: true; code: string; expiresAt: string} | {ok: false; reason: 'unavailable' | 'not-found' | 'frozen'};

const ticksPerHour = 36000000000n;
const codeText = /^[A-Za-z0-9_-]{43}$/;

/**
 * Issues a single-use recovery code for an agent owned by this (not frozen) human, valid for 10 minutes. Only the code's SHA-256
 * is stored, and the agent's earlier code, used or not, is replaced. The human-side caller (a later owner action) is responsible
 * for fresh re-authentication.
 */
export async function issueRecoveryCode(env: Env, input: {humanId: string; agentId: string}, clock: () => bigint = nowUtc): Promise<IssueRecoveryCodeResult> {
    if (writesRefused(env)) return {ok: false, reason: 'unavailable'};
    const code = base64url(crypto.getRandomValues(new Uint8Array(32))), codeHash = await hash(code);
    return atomic(env.db, async tx => {
        const at = clock(), stamp = at.toString(), expiresAt = at + recoveryCodeTicks;
        const owner = await tx.db.prepare('SELECT a.human_id,h.frozen_at FROM accounts a JOIN humans h ON h.human_id=a.human_id WHERE a.account_id=?')
            .bind(input.agentId).first<{human_id: string; frozen_at: string | null}>();
        if (!owner || owner.human_id !== input.humanId) return {ok: false as const, reason: 'not-found' as const};
        if (owner.frozen_at !== null) return {ok: false as const, reason: 'frozen' as const};
        tx.writes.push(tx.db.prepare(`INSERT INTO recovery_codes(account_id,code_hash,human_id,created_at,expires_at,used_at,updated_at)
            VALUES(?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER),NULL,CAST(? AS INTEGER))
            ON CONFLICT(account_id) DO UPDATE SET code_hash=excluded.code_hash,human_id=excluded.human_id,created_at=excluded.created_at,
            expires_at=excluded.expires_at,used_at=NULL,updated_at=excluded.updated_at`).bind(input.agentId, codeHash, input.humanId, stamp, expiresAt.toString(), stamp));
        return {ok: true as const, code, expiresAt: apiTime(expiresAt)!};
    });
}

/** Counts every attempt per source in fixed UTC hours, before any code lookup, so codes cannot be guessed at volume. */
async function admitRecoveryAttempt(env: Env, request: Request, clock: () => bigint): Promise<void> {
    // Cloudflare supplies CF-Connecting-IP at the deployed ingress; local callers can spoof it.
    const source = await hash('key-recovery:' + (request.headers.get('CF-Connecting-IP') ?? 'unknown'));
    await atomic(env.db, async tx => {
        const at = clock(), hour = Number(at / ticksPerHour);
        const counter = await tx.get('recovery-rate', '', source);
        const count = counter?.hour === hour ? counter.count : 0;
        if (count >= recoveryAttemptsPerSourcePerHour) fail(429, 'capacity', Math.max(1, Math.ceil(Number(BigInt(hour + 1) * ticksPerHour - at) / 1e7)));
        tx.put('recovery-rate', '', source, {hour, count: count + 1});
        tx.writes.push(tx.db.prepare("DELETE FROM rate_limits WHERE seq IN (SELECT seq FROM rate_limits WHERE policy='key-recovery' AND window_start<CAST(? AS INTEGER) LIMIT 100)")
            .bind((BigInt(hour) * ticksPerHour).toString()));
    });
}

/**
 * POST /v1/key-requests: the owner-issued code adds the DPoP proof key to the agent as a live key, without cooling, because the
 * owner already authorised it. Unknown agents and wrong, expired, used or revoked-owner codes are one identical 404, with nothing
 * written; key-in-use and the live-key limit are reported only after the code is accepted.
 */
export async function recoverAgentKey(env: Env, request: Request, input: unknown, clock: () => bigint = nowUtc): Promise<AgentKeySummary> {
    if (request.headers.has('Authorization')) fail();
    // No access token: a proof carrying `ath` is refused. publicJwk refuses private (`d`) members; nothing is stored or echoed.
    const proof = await verifyDpop(request, {now: epochSeconds(clock())});
    if (!proof.ok) fail(401, 'unauthenticated');
    object(input, ['agentId', 'code'], ['label']);
    const {agentId, code, label: labelValue} = input as {agentId: unknown; code: unknown; label?: unknown};
    if (typeof agentId !== 'string') invalidField('agentId', 'must be an agent ID');
    if (typeof code !== 'string') invalidField('code', 'must be the recovery code from the owner');
    const label = validLabel(labelValue);
    await admitRecoveryAttempt(env, request, clock);
    const codeHash = codeText.test(code) ? await hash(code) : null;
    return atomic(env.db, async tx => {
        const at = clock(), stamp = at.toString();
        const grant = codeHash === null ? null : await tx.db.prepare(`SELECT r.account_id,r.human_id,CAST(r.expires_at AS TEXT) expires_at,r.used_at IS NOT NULL used,
                a.human_id owner,h.frozen_at IS NOT NULL frozen
            FROM recovery_codes r JOIN accounts a ON a.account_id=r.account_id JOIN humans h ON h.human_id=r.human_id WHERE r.code_hash=?`)
            .bind(codeHash).first<{account_id: string; human_id: string; expires_at: string; used: number; owner: string | null; frozen: number}>();
        if (!grant || grant.account_id !== agentId || grant.used !== 0 || exactInt64(grant.expires_at) <= at || grant.owner !== grant.human_id || grant.frozen !== 0)
            fail(404, 'unavailable');
        const existing = await findKey(tx.db, proof.jkt);
        if (existing && (existing.accountId !== agentId || existing.revokedAt !== null)) conflict('proof', 'key-in-use');
        if (!existing && await countLiveKeys(tx, agentId) >= maxLiveKeysPerAgent) fail(429, 'capacity');
        tx.writes.push(tx.db.prepare('UPDATE recovery_codes SET used_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER) WHERE account_id=? AND code_hash=? AND used_at IS NULL')
            .bind(stamp, stamp, agentId, codeHash));
        // A key already live on this agent only spends the code.
        if (existing) return keySummary(existing);
        insertKey(tx, {kid: proof.jkt, accountId: agentId, jwk: proof.jwk, label, coolingUntil: null});
        return keySummary({kid: proof.jkt, accountId: agentId, jwk: proof.jwk, label, createdAt: tx.store.now, lastUsedAt: null, revokedAt: null, coolingUntil: null});
    });
}
