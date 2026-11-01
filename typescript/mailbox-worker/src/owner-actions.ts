import { atomic, type Env, type Transaction } from './database.ts';
import { confirmKeyRequest, revokeKeyForOwner } from './agent-keys.ts';
import { confirmBindingRequest, liveKeysDigest, pendingBindingRequestsPerHuman, rejectBindingRequest } from './binding-requests.ts';
import { maxAgentsPerHuman, requireReadable, writesRefused } from './humans.ts';
import { rejectKeyRequest } from './key-requests.ts';
import { issueRecoveryCode } from './recovery-codes.ts';
import { apiTime, nowUtc } from './time.ts';

/** A signed-in human may not decide on a session older than this, or one dated further ahead than the skew. */
export const reauthWindowMs = 600_000;
export const reauthFutureSkewMs = 60_000;

export type OwnerKey = {kid: string; label: string | null; createdAt: string; lastUsedAt: string | null; coolingUntil: string | null};
export type OwnerBindingRequest = {requestId: string; agentId: string; displayName: string | null; requestingKid: string; liveKeys: OwnerKey[]; keysDigest: string; createdAt: string; expiresAt: string};
export type OwnerKeyRequest = {requestId: string; kid: string; label: string | null; endorsedBy: string; createdAt: string; expiresAt: string};
export type OwnedAgentKeys = {agentId: string; displayName: string | null; keys: OwnerKey[]; keyRequests: OwnerKeyRequest[]};
export type OwnerOverview = {frozen: true} | {frozen: false; bindingRequests: OwnerBindingRequest[]; agents: OwnedAgentKeys[]};

export type OwnerAction = {humanId: string; authAt: number} & (
    | {kind: 'confirm-binding'; requestId: string; keysDigest: string}
    | {kind: 'reject-binding' | 'confirm-key' | 'reject-key'; requestId: string}
    | {kind: 'revoke-key'; agentId: string; kid: string}
    | {kind: 'issue-recovery-code'; agentId: string});
export type OwnerFailureReason = 'stale-authentication' | 'frozen' | 'not-found' | 'not-pending' | 'expired' | 'agent-limit' | 'already-owned' | 'key-in-use'
    | 'key-limit' | 'endorser-revoked' | 'requesting-key-revoked' | 'keys-changed' | 'unavailable';
export type OwnerFailure = {ok: false; reason: OwnerFailureReason};
/** What the human-side functions of the owning modules return; `ownerAction` adds the recovery code variant. */
export type OwnerDecision = {ok: true} | OwnerFailure;
export type OwnerActionResult = OwnerDecision | {ok: true; code: string; expiresAt: string};
export type OwnerHuman = {humanId: string; subject: string};

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const kidPattern = /^[A-Za-z0-9_-]{43}$/;
const digestPattern = /^[A-Za-z0-9_-]{43}$/;
const destructive = new Set<string>(['confirm-binding', 'confirm-key', 'revoke-key', 'issue-recovery-code']);
const notFound: OwnerFailure = {ok: false, reason: 'not-found'};
const unavailable: OwnerFailure = {ok: false, reason: 'unavailable'};

type KeyRecord = {account_id: string; kid: string; label: string | null; created_at: string; last_used_at: string | null; cooling_until: string | null};
const ownerKey = (row: KeyRecord): OwnerKey => ({kid: row.kid, label: row.label, createdAt: apiTime(row.created_at)!, lastUsedAt: apiTime(row.last_used_at), coolingUntil: apiTime(row.cooling_until)});
const placeholders = (values: unknown[]) => values.map(() => '?').join(',');

/** Pending requests and the keys of the human's agents. An unknown human sees nothing; a frozen one sees only that. */
export async function ownerOverview(env: Env, humanId: string, clock: () => bigint = nowUtc): Promise<OwnerOverview> {
    requireReadable(env);
    const empty: OwnerOverview = {frozen: false, bindingRequests: [], agents: []};
    if (typeof humanId !== 'string' || !uuidPattern.test(humanId)) return empty;
    return atomic(env.db, async tx => {
        const now = clock().toString();
        const human = await tx.db.prepare('SELECT subject,frozen_at FROM humans WHERE human_id=?').bind(humanId).first<{subject: string; frozen_at: string | null}>();
        if (!human) return empty;
        if (human.frozen_at !== null) return {frozen: true as const};
        const bindings = (await tx.db.prepare(`SELECT b.request_id,b.account_id,a.display_name,b.kid,CAST(b.created_at AS TEXT) created_at,CAST(b.expires_at AS TEXT) expires_at
            FROM binding_requests b JOIN accounts a ON a.account_id=b.account_id
            WHERE b.target_subject=? AND b.status='pending' AND b.expires_at>CAST(? AS INTEGER) ORDER BY b.created_at,b.request_id LIMIT ?`)
            .bind(human.subject, now, pendingBindingRequestsPerHuman).all<{request_id: string; account_id: string; display_name: string | null; kid: string; created_at: string; expires_at: string}>()).results;
        const owned = (await tx.db.prepare('SELECT account_id,display_name FROM accounts WHERE human_id=? ORDER BY created_at,rowid LIMIT ?')
            .bind(humanId, maxAgentsPerHuman).all<{account_id: string; display_name: string | null}>()).results;
        const accountIds = [...new Set([...bindings.map(row => row.account_id), ...owned.map(row => row.account_id)])];
        const keys = accountIds.length === 0 ? [] : (await tx.db.prepare(`SELECT account_id,kid,label,CAST(created_at AS TEXT) created_at,CAST(last_used_at AS TEXT) last_used_at,
            CAST(cooling_until AS TEXT) cooling_until FROM agent_keys WHERE account_id IN (${placeholders(accountIds)}) AND revoked_at IS NULL ORDER BY created_at,kid`)
            .bind(...accountIds).all<KeyRecord>()).results;
        const ownedIds = owned.map(row => row.account_id);
        const requests = ownedIds.length === 0 ? [] : (await tx.db.prepare(`SELECT request_id,account_id,kid,label,endorsed_by,CAST(created_at AS TEXT) created_at,CAST(expires_at AS TEXT) expires_at
            FROM key_requests WHERE account_id IN (${placeholders(ownedIds)}) AND status='pending' AND expires_at>CAST(? AS INTEGER) ORDER BY created_at,request_id`)
            .bind(...ownedIds, now).all<{request_id: string; account_id: string; kid: string; label: string | null; endorsed_by: string; created_at: string; expires_at: string}>()).results;
        const bindingRequests: OwnerBindingRequest[] = [];
        for (const row of bindings) {
            const liveKeys = keys.filter(key => key.account_id === row.account_id).map(ownerKey);
            bindingRequests.push({requestId: row.request_id, agentId: row.account_id, displayName: row.display_name, requestingKid: row.kid, liveKeys,
                keysDigest: await liveKeysDigest(liveKeys.map(key => key.kid)), createdAt: apiTime(row.created_at)!, expiresAt: apiTime(row.expires_at)!});
        }
        return {
            frozen: false as const,
            bindingRequests,
            agents: owned.map(agent => ({agentId: agent.account_id, displayName: agent.display_name,
                keys: keys.filter(key => key.account_id === agent.account_id).map(ownerKey),
                keyRequests: requests.filter(row => row.account_id === agent.account_id).map(row => ({requestId: row.request_id, kid: row.kid, label: row.label,
                    endorsedBy: row.endorsed_by, createdAt: apiTime(row.created_at)!, expiresAt: apiTime(row.expires_at)!}))})),
        };
    });
}

/** The action's shape, or null for anything malformed (reported as not-found, never as a distinct error). */
function validShape(input: unknown): OwnerAction | null {
    if (typeof input !== 'object' || input === null) return null;
    const action = input as Record<string, unknown>;
    const uuid = (value: unknown) => typeof value === 'string' && uuidPattern.test(value);
    if (!uuid(action.humanId) || typeof action.authAt !== 'number' || !Number.isSafeInteger(action.authAt)) return null;
    switch (action.kind) {
        case 'confirm-binding': return uuid(action.requestId) && typeof action.keysDigest === 'string' && digestPattern.test(action.keysDigest) ? input as OwnerAction : null;
        case 'reject-binding': case 'confirm-key': case 'reject-key': return uuid(action.requestId) ? input as OwnerAction : null;
        case 'revoke-key': return uuid(action.agentId) && typeof action.kid === 'string' && kidPattern.test(action.kid) ? input as OwnerAction : null;
        case 'issue-recovery-code': return uuid(action.agentId) ? input as OwnerAction : null;
        default: return null;
    }
}

type DecidedAction = Exclude<OwnerAction, {kind: 'issue-recovery-code'}>;

async function decide(tx: Transaction, input: DecidedAction, at: bigint): Promise<OwnerDecision> {
    const human = await tx.db.prepare('SELECT subject,frozen_at FROM humans WHERE human_id=?').bind(input.humanId).first<{subject: string; frozen_at: string | null}>();
    if (!human) return notFound;
    if (human.frozen_at !== null) return {ok: false, reason: 'frozen'};
    switch (input.kind) {
        case 'confirm-binding': return confirmBindingRequest(tx, {humanId: input.humanId, subject: human.subject}, input.requestId, input.keysDigest, at);
        case 'reject-binding': return rejectBindingRequest(tx, {humanId: input.humanId, subject: human.subject}, input.requestId, at);
        case 'confirm-key': return confirmKeyRequest(tx, input.humanId, input.requestId, at);
        case 'reject-key': return rejectKeyRequest(tx, input.humanId, input.requestId, at);
        case 'revoke-key': return revokeKeyForOwner(tx, input.humanId, input.agentId, input.kid, at);
    }
}

/**
 * One owner decision, in a single transaction that re-reads the human. A malformed action is `not-found`; a destructive one on a
 * session that is too old (or dated in the future) is `stale-authentication` before anything is read. Storage failures are
 * `unavailable`, never thrown to the authorization server.
 */
export async function ownerAction(env: Env, input: OwnerAction, clock: () => bigint = nowUtc): Promise<OwnerActionResult> {
    if (writesRefused(env)) return unavailable;
    const action = validShape(input);
    if (!action) return notFound;
    const at = clock();
    if (destructive.has(action.kind)) {
        const nowMs = Number(at / 10000n);
        if (nowMs - action.authAt > reauthWindowMs || action.authAt > nowMs + reauthFutureSkewMs) return {ok: false, reason: 'stale-authentication'};
    }
    try {
        if (action.kind === 'issue-recovery-code') return await issueRecoveryCode(env, {humanId: action.humanId, agentId: action.agentId}, clock);
        // The transaction time is the injected clock, so key rows and the churn window of confirm-key use the same time as every decision.
        return await atomic(env.db, tx => decide(tx, action, tx.store.now), clock);
    } catch {
        console.error('owner-action: storage failure');
        return unavailable;
    }
}
