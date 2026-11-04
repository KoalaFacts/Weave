/**
 * Governed external actions: an owned agent proposes one effect (for example a post) through one of its credentials, the
 * agent's human owner approves that exact plan, and the agent then executes it at most once. The plan digest binds the
 * provider and its contract revision, the operation and full content, the credential reference and revision, and the
 * provider-verified target account. Any change after approval refuses dispatch; an unrecorded outcome is never retried.
 */
import * as schema from './schema.ts';
import type { AgentAuth } from './agent-tokens.ts';
import { findKey } from './agent-keys.ts';
import { base64url } from './jose.ts';
import { apiTime, exactInt64, nowUtc } from './time.ts';
import { atomic, type Env, type Transaction } from './database.ts';
import { writesRefused } from './humans.ts';
import type { OwnerDecision, OwnerHuman } from './owner-actions.ts';
import { provider as findProvider } from './external-providers.ts';
import { Problem, fail, invalidField, object } from './wire.ts';

export const actionLifetimeTicks = 36_000_000_000n; // 1 h to approve and execute
export const pendingActionsPerAgent = 20;
export const maxContentLength = 3000;

export type ActionStatus = 'pending' | 'approved' | 'rejected' | 'dispatching' | 'completed' | 'failed' | 'unknown';
export type ExternalActionView = {actionId: string; provider: string; operation: string; content: string; credentialRef: string;
    credentialRevision: string; targetAccount: string; status: ActionStatus | 'expired'; createdAt: string; expiresAt: string;
    decidedAt: string | null; dispatchedAt: string | null; completedAt: string | null; receipt: string | null; failure: string | null};
/** What the owner page shows for a pending action; `planDigest` is what the owner confirms. */
export type OwnerExternalAction = {actionId: string; agentId: string; provider: string; operation: string; content: string;
    credentialRef: string; credentialRevision: string; targetAccount: string; targetDisplay: string; planDigest: string; createdAt: string; expiresAt: string};

type Row = {action_id: string; account_id: string; provider: string; contract_revision: string; operation: string; content: string;
    credential_ref: string; credential_revision: string; target_account: string; plan_digest: string; status: string; created_at: string;
    expires_at: string; decided_at: string | null; decided_by: string | null; dispatched_at: string | null; completed_at: string | null;
    receipt: string | null; failure: string | null};
type Credential = {provider: string; revision: string; target_account: string; target_display: string; revoked_at: string | null};

const statuses = new Set<string>(['pending', 'approved', 'rejected', 'dispatching', 'completed', 'failed', 'unknown']);
const credentialRefPattern = /^[A-Za-z0-9._:-]{1,64}$/;
const columns = `action_id,account_id,provider,contract_revision,operation,content,credential_ref,credential_revision,target_account,plan_digest,status,
    CAST(created_at AS TEXT) created_at,CAST(expires_at AS TEXT) expires_at,CAST(decided_at AS TEXT) decided_at,decided_by,
    CAST(dispatched_at AS TEXT) dispatched_at,CAST(completed_at AS TEXT) completed_at,receipt,failure`;

function conflict(reason: string): never { throw new Problem(409, 'conflict', undefined, {field: 'action', reason}); }

/** The plan the owner approves: every field whose change would make it a different effect. */
export async function planDigest(row: Pick<Row, 'action_id' | 'account_id' | 'provider' | 'contract_revision' | 'operation' | 'content'
    | 'credential_ref' | 'credential_revision' | 'target_account' | 'expires_at'>): Promise<string> {
    const text = JSON.stringify(['weave-external-action-v1', row.action_id, row.account_id, row.provider, row.contract_revision, row.operation,
        row.content, row.credential_ref, row.credential_revision, row.target_account, row.expires_at]);
    return base64url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text))));
}

function view(row: Row, now: bigint): ExternalActionView {
    // Only these statuses are written; anything else is storage corruption.
    if (!statuses.has(row.status)) fail(503, 'storage');
    const live = row.status === 'pending' || row.status === 'approved';
    return {actionId: row.action_id, provider: row.provider, operation: row.operation, content: row.content, credentialRef: row.credential_ref,
        credentialRevision: row.credential_revision, targetAccount: row.target_account,
        status: live && exactInt64(row.expires_at) <= now ? 'expired' : row.status as ActionStatus,
        createdAt: apiTime(row.created_at)!, expiresAt: apiTime(row.expires_at)!, decidedAt: apiTime(row.decided_at),
        dispatchedAt: apiTime(row.dispatched_at), completedAt: apiTime(row.completed_at), receipt: row.receipt, failure: row.failure};
}

const readRow = (tx: Transaction, actionId: string) =>
    tx.db.prepare(`SELECT ${columns} FROM external_actions WHERE action_id=?`).bind(actionId).first<Row>();
const readCredential = (tx: Transaction, accountId: string, credentialRef: string) =>
    tx.db.prepare('SELECT provider,revision,target_account,target_display,CAST(revoked_at AS TEXT) revoked_at FROM external_credentials WHERE account_id=? AND credential_ref=?')
        .bind(accountId, credentialRef).first<Credential>();

/**
 * The authenticated agent and the check that its authority is still live, run inside every transaction of an operation:
 * its key for the HTTP API, its consent grant for MCP.
 */
export type ActionCaller = {accountId: string; check: (tx: Transaction) => Promise<void>};

/** The HTTP API caller: the DPoP-bound key must still be live. */
export const keyCaller = (auth: AgentAuth): ActionCaller => ({accountId: auth.accountId, check: async tx => {
    const key = await findKey(tx.db, auth.kid);
    if (!key || key.revokedAt !== null || key.accountId !== auth.accountId) fail(401, 'unauthenticated');
}});

async function ownAction(tx: Transaction, caller: ActionCaller, actionId: string): Promise<Row> {
    const row = await readRow(tx, actionId);
    // Another agent's action is indistinguishable from a missing one.
    if (!row || row.account_id !== caller.accountId) fail(404, 'unavailable');
    return row;
}

/** POST /v1/external-actions: records a pending plan. Nothing is sent to the provider. */
export async function proposeExternalAction(env: Env, caller: ActionCaller, input: unknown, clock: () => bigint = nowUtc): Promise<ExternalActionView> {
    if (writesRefused(env)) fail(503, 'maintenance', 30);
    object(input, ['credentialRef', 'operation', 'content']);
    const {credentialRef, operation, content} = input as Record<string, unknown>;
    if (typeof credentialRef !== 'string' || !credentialRefPattern.test(credentialRef)) invalidField('credentialRef', 'must be 1 to 64 characters of letters, digits, ".", "_", ":" or "-"');
    if (typeof operation !== 'string') invalidField('operation', 'must be a supported operation');
    if (typeof content !== 'string' || content.trim() === '' || content.length > maxContentLength) invalidField('content', `must be non-blank text of at most ${maxContentLength} characters`);
    return atomic(env.db, async tx => {
        const at = tx.store.now;
        await caller.check(tx);
        const owner = await tx.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(caller.accountId).first<{human_id: string | null}>();
        if (!owner) fail(401, 'unauthenticated');
        // Only an owned agent has someone who can approve.
        if (owner.human_id === null) conflict('unowned-agent');
        const credential = await readCredential(tx, caller.accountId, credentialRef);
        if (!credential || credential.revoked_at !== null) invalidField('credentialRef', 'must name a live credential of this agent');
        const provider = findProvider(env, credential.provider);
        if (!provider) conflict('provider-unavailable');
        if (!provider.operations.includes(operation)) invalidField('operation', 'must be a supported operation');
        const waiting = await tx.db.prepare(`SELECT count(*) n FROM external_actions WHERE account_id=? AND status IN ('pending','approved') AND expires_at>CAST(? AS INTEGER)`)
            .bind(caller.accountId, at.toString()).first<number>('n');
        if (waiting === null) fail(503, 'storage');
        if (waiting >= pendingActionsPerAgent) fail(429, 'capacity');
        const plan = {action_id: crypto.randomUUID(), account_id: caller.accountId, provider: provider.name, contract_revision: provider.contractRevision,
            operation, content, credential_ref: credentialRef, credential_revision: credential.revision, target_account: credential.target_account,
            expires_at: (at + actionLifetimeTicks).toString()};
        const digest = await planDigest(plan);
        tx.store.stage(tx.store.orm.insert(schema.externalActions).values({...plan, plan_digest: digest, status: 'pending',
            created_at: at, expires_at: at + actionLifetimeTicks, updated_at: at}));
        return view({...plan, plan_digest: digest, status: 'pending', created_at: at.toString(), decided_at: null, decided_by: null,
            dispatched_at: null, completed_at: null, receipt: null, failure: null}, at);
    });
}

/** GET /v1/external-actions/{actionId}. */
export async function getExternalAction(env: Env, caller: ActionCaller, actionId: string): Promise<ExternalActionView> {
    return atomic(env.db, async tx => { await caller.check(tx); return view(await ownAction(tx, caller, actionId), tx.store.now); });
}

type Claim = {row: Row; dispatch: true} | {row: Row; dispatch: false};

/**
 * Re-checks the approved plan against current state and commits 'dispatching' before any provider call. A plan that no
 * longer holds becomes 'failed' with nothing sent; anything not approved is returned unchanged, so a retry never re-sends.
 */
async function claim(env: Env, caller: ActionCaller, actionId: string, clock: () => bigint): Promise<Claim> {
    return atomic(env.db, async tx => {
        const at = tx.store.now, stamp = at.toString();
        await caller.check(tx);
        const row = await ownAction(tx, caller, actionId);
        if (row.status !== 'approved') return {row, dispatch: false};
        if (exactInt64(row.expires_at) <= at) return {row, dispatch: false};
        const refuse = (reason: string): Claim => {
            tx.writes.push(tx.db.prepare(`UPDATE external_actions SET status='failed',failure=?,completed_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER)
                WHERE action_id=? AND status='approved'`).bind(reason, stamp, stamp, actionId));
            return {row: {...row, status: 'failed', failure: reason, completed_at: stamp}, dispatch: false};
        };
        const owner = await tx.db.prepare('SELECT a.human_id,CAST(h.frozen_at AS TEXT) frozen_at FROM accounts a LEFT JOIN humans h ON h.human_id=a.human_id WHERE a.account_id=?')
            .bind(caller.accountId).first<{human_id: string | null; frozen_at: string | null}>();
        if (!owner || owner.human_id !== row.decided_by) return refuse('owner-changed');
        if (owner.frozen_at !== null) return refuse('owner-frozen');
        const credential = await readCredential(tx, caller.accountId, row.credential_ref);
        if (!credential || credential.revoked_at !== null || credential.revision !== row.credential_revision
            || credential.provider !== row.provider || credential.target_account !== row.target_account) return refuse('credential-changed');
        const provider = findProvider(env, row.provider);
        if (!provider || provider.contractRevision !== row.contract_revision || await planDigest(row) !== row.plan_digest) return refuse('plan-changed');
        tx.writes.push(tx.db.prepare(`UPDATE external_actions SET status='dispatching',dispatched_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER)
            WHERE action_id=? AND status='approved'`).bind(stamp, stamp, actionId));
        return {row: {...row, status: 'dispatching', dispatched_at: stamp}, dispatch: true};
    }, clock);
}

/** POST /v1/external-actions/{actionId}/execute: dispatches an approved plan once and records the provider's outcome. */
export async function executeExternalAction(env: Env, caller: ActionCaller, actionId: string, clock: () => bigint = nowUtc): Promise<ExternalActionView> {
    if (writesRefused(env)) fail(503, 'maintenance', 30);
    const claimed = await claim(env, caller, actionId, clock);
    if (!claimed.dispatch) return view(claimed.row, clock());
    const row = claimed.row;
    // claim() verified the provider and its contract revision in the committed transaction.
    const provider = findProvider(env, row.provider)!;
    let status: 'completed' | 'failed' | 'unknown', receipt: string | null = null, failure: string | null = null;
    try {
        const result = await provider.execute(env, {operation: row.operation, content: row.content, targetAccount: row.target_account, actionId: row.action_id});
        if (result.status === 'completed') { status = 'completed'; receipt = result.receipt; }
        else { status = 'failed'; failure = 'provider-rejected: ' + result.reason; }
    } catch {
        // The effect may have happened. Never report this as failed or dispatch it again.
        status = 'unknown'; failure = 'outcome-unknown';
    }
    try {
        return await atomic(env.db, async tx => {
            const stamp = tx.store.now.toString();
            tx.writes.push(tx.db.prepare(`UPDATE external_actions SET status=?,receipt=?,failure=?,completed_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER)
                WHERE action_id=? AND status='dispatching'`).bind(status, receipt, failure, stamp, stamp, actionId));
            return view({...row, status, receipt, failure, completed_at: stamp}, tx.store.now);
        }, clock);
    } catch {
        // The outcome could not be recorded; the row stays 'dispatching' and is never dispatched again.
        console.error('external-action: outcome not recorded');
        return view(row, clock());
    }
}

/** Pending actions of the human's agents, for the owner page. */
export async function pendingForOwner(tx: Transaction, agentIds: string[], now: bigint): Promise<OwnerExternalAction[]> {
    if (agentIds.length === 0) return [];
    // The display name is the credential's current one; the confirmation re-checks the revision and target it was proposed with.
    const rows = (await tx.db.prepare(`SELECT x.action_id,x.account_id,x.provider,x.operation,x.content,x.credential_ref,x.credential_revision,
        x.target_account,x.plan_digest,CAST(x.created_at AS TEXT) created_at,CAST(x.expires_at AS TEXT) expires_at,c.target_display
        FROM external_actions x JOIN external_credentials c ON c.account_id=x.account_id AND c.credential_ref=x.credential_ref
        WHERE x.account_id IN (${agentIds.map(() => '?').join(',')}) AND x.status='pending' AND x.expires_at>CAST(? AS INTEGER)
        ORDER BY x.created_at,x.action_id LIMIT ?`)
        .bind(...agentIds, now.toString(), pendingActionsPerAgent * agentIds.length).all<Row & {target_display: string}>()).results;
    return rows.map(row => ({actionId: row.action_id, agentId: row.account_id, provider: row.provider, operation: row.operation, content: row.content,
        credentialRef: row.credential_ref, credentialRevision: row.credential_revision, targetAccount: row.target_account, targetDisplay: row.target_display,
        planDigest: row.plan_digest, createdAt: apiTime(row.created_at)!, expiresAt: apiTime(row.expires_at)!}));
}

/** The action of an agent this human owns; any other is indistinguishable from a missing one. */
async function ownedByHuman(tx: Transaction, human: OwnerHuman, actionId: string): Promise<Row | null> {
    const row = await readRow(tx, actionId);
    if (!row) return null;
    const owner = await tx.db.prepare('SELECT human_id FROM accounts WHERE account_id=?').bind(row.account_id).first<{human_id: string | null}>();
    return owner?.human_id === human.humanId ? row : null;
}

/** Owner approval of the exact plan the page showed. The approval adds no authority: the agent must still execute it. */
export async function confirmExternalAction(tx: Transaction, human: OwnerHuman, actionId: string, digest: string, at: bigint): Promise<OwnerDecision> {
    const row = await ownedByHuman(tx, human, actionId);
    if (!row) return {ok: false, reason: 'not-found'};
    if (row.status !== 'pending') return {ok: false, reason: 'not-pending'};
    if (exactInt64(row.expires_at) <= at) return {ok: false, reason: 'expired'};
    if (digest !== row.plan_digest) return {ok: false, reason: 'plan-changed'};
    const credential = await readCredential(tx, row.account_id, row.credential_ref);
    if (!credential || credential.revoked_at !== null || credential.revision !== row.credential_revision
        || credential.target_account !== row.target_account) return {ok: false, reason: 'plan-changed'};
    const stamp = at.toString();
    tx.writes.push(tx.db.prepare(`UPDATE external_actions SET status='approved',decided_at=CAST(? AS INTEGER),decided_by=?,updated_at=CAST(? AS INTEGER)
        WHERE action_id=? AND status='pending'`).bind(stamp, human.humanId, stamp, actionId));
    return {ok: true};
}

/** A pending action is rejected even after its expiry; rejecting needs no fresh authentication. */
export async function rejectExternalAction(tx: Transaction, human: OwnerHuman, actionId: string, at: bigint): Promise<OwnerDecision> {
    const row = await ownedByHuman(tx, human, actionId);
    if (!row) return {ok: false, reason: 'not-found'};
    if (row.status !== 'pending') return {ok: false, reason: 'not-pending'};
    const stamp = at.toString();
    tx.writes.push(tx.db.prepare(`UPDATE external_actions SET status='rejected',decided_at=CAST(? AS INTEGER),decided_by=?,updated_at=CAST(? AS INTEGER)
        WHERE action_id=? AND status='pending'`).bind(stamp, human.humanId, stamp, actionId));
    return {ok: true};
}
