import { bindingColumns, bindingRow, bindingView, type BindingStatus, type StoredBinding } from './binding-requests.ts';
import { keyRequestColumns, keyRequestRow, keyRequestView, type KeyRequestStatus, type StoredKeyRequest } from './key-requests.ts';
import { effectiveStatus, type ActionStatus } from './external-actions.ts';
import { apiInstant, exactInt64 } from './time.ts';
import type { Transaction } from './database.ts';

export const noticeWindowTicks = 864000000000n; // 24 h
export const maxNotices = 50;

export type Notice =
    | {kind: 'key-added'; kid: string; label: string | null; createdAt: string; coolingUntil: string | null}
    | {kind: 'key-revoked'; kid: string; revokedAt: string}
    | {kind: 'key-request'; requestId: string; kid: string; endorsedBy: string; status: KeyRequestStatus; createdAt: string; expiresAt: string; decidedAt: string | null}
    | {kind: 'binding-request'; requestId: string; human: string; status: BindingStatus; createdAt: string; expiresAt: string; decidedAt: string | null}
    | {kind: 'external-action'; actionId: string; status: ActionStatus | 'expired'; credentialRef: string; operation: string; createdAt: string;
        expiresAt: string; decidedAt: string | null; completedAt: string | null};

type StoredAction = {action_id: string; status: string; credential_ref: string; operation: string; created_at: string; expires_at: string;
    decided_at: string | null; completed_at: string | null; at: string};
const optionalInstant = (ticks: string | null) => ticks === null ? null : apiInstant(exactInt64(ticks));

type StoredKey = {kid: string; label: string | null; created_at: string; revoked_at: string | null; cooling_until: string | null};

/**
 * Derived only from this account's own rows; nothing is stored as a message. The registration key (inserted in the same transaction
 * as the account, so created at the account's time) is not reported as an addition; every later key is, including the first
 * recovery key of an agent created without one. A key made by confirming a key request is reported once, by that request's
 * `key-request` notice. A pending request past its expiry is reported as expired, at its expiry time. An external action is
 * reported once, with its current status (the same status `GET /v1/external-actions/{id}` returns), at its latest change; a
 * pending or approved action past its expiry reads as expired, at its expiry time. Its content is never included.
 */
export async function accountNotices(tx: Transaction, accountId: string, now: bigint): Promise<Notice[]> {
    const since = (now - noticeWindowTicks).toString();
    const entries: {at: bigint; notice: Notice}[] = [];

    // Each source loads only its newest `maxNotices` rows, ordered by the same instant as the merge sort below (`at`): the merged
    // newest 50 are then exact, because any entry of the global top 50 is in its own source's top 50.
    const keys = await tx.db.prepare(`SELECT k.kid,k.label,CAST(k.created_at AS TEXT) created_at,CAST(k.revoked_at AS TEXT) revoked_at,
            CAST(k.cooling_until AS TEXT) cooling_until
        FROM agent_keys k WHERE k.account_id=?1 AND ((k.revoked_at IS NULL AND k.created_at>CAST(?2 AS INTEGER)
            AND k.created_at<>(SELECT a.created_at FROM accounts a WHERE a.account_id=?1)
            AND NOT EXISTS (SELECT 1 FROM key_requests r WHERE r.account_id=?1 AND r.kid=k.kid AND r.status='confirmed'))
            OR k.revoked_at>CAST(?2 AS INTEGER))
        ORDER BY max(k.created_at,coalesce(k.revoked_at,0)) DESC LIMIT ?3`).bind(accountId, since, maxNotices).all<StoredKey>();
    for (const row of keys.results) {
        if (row.revoked_at !== null) {
            const at = exactInt64(row.revoked_at);
            entries.push({at, notice: {kind: 'key-revoked', kid: row.kid, revokedAt: apiInstant(at)}});
        } else {
            const at = exactInt64(row.created_at);
            entries.push({at, notice: {kind: 'key-added', kid: row.kid, label: row.label, createdAt: apiInstant(at),
                coolingUntil: row.cooling_until === null ? null : apiInstant(exactInt64(row.cooling_until))}});
        }
    }

    // Pending (live, or expired but created in the window) or decided in the window.
    const requestFilter = `account_id=? AND (decided_at>CAST(? AS INTEGER) OR (status='pending' AND created_at>CAST(? AS INTEGER)))`;
    const keyRequests = await tx.db.prepare(`SELECT ${keyRequestColumns} FROM key_requests WHERE ${requestFilter}
        ORDER BY coalesce(decided_at,created_at) DESC LIMIT ?`).bind(accountId, since, since, maxNotices).all<StoredKeyRequest>();
    for (const stored of keyRequests.results) {
        const row = keyRequestRow(stored), view = keyRequestView(row, now);
        const at = view.status === 'expired' && row.status === 'pending' ? row.expiresAt : row.decidedAt ?? row.createdAt;
        entries.push({at, notice: {kind: 'key-request', requestId: view.requestId, kid: view.kid, endorsedBy: view.endorsedBy, status: view.status,
            createdAt: view.createdAt, expiresAt: view.expiresAt, decidedAt: view.decidedAt}});
    }
    const bindings = await tx.db.prepare(`SELECT ${bindingColumns} FROM binding_requests WHERE ${requestFilter}
        ORDER BY coalesce(decided_at,created_at) DESC LIMIT ?`).bind(accountId, since, since, maxNotices).all<StoredBinding>();
    for (const stored of bindings.results) {
        const row = bindingRow(stored), view = bindingView(row, now);
        const at = view.status === 'expired' && row.status === 'pending' ? row.expiresAt : row.decidedAt ?? row.createdAt;
        entries.push({at, notice: {kind: 'binding-request', requestId: view.requestId, human: view.human, status: view.status,
            createdAt: view.createdAt, expiresAt: view.expiresAt, decidedAt: view.decidedAt}});
    }
    // An action's latest change: its last write, or its expiry when a live plan lapsed with nothing written.
    const live = `status IN ('pending','approved') AND expires_at<=CAST(?3 AS INTEGER)`;
    const actions = await tx.db.prepare(`SELECT action_id,status,credential_ref,operation,CAST(created_at AS TEXT) created_at,
            CAST(expires_at AS TEXT) expires_at,CAST(decided_at AS TEXT) decided_at,CAST(completed_at AS TEXT) completed_at,
            CAST(CASE WHEN ${live} THEN expires_at ELSE updated_at END AS TEXT) at
        FROM external_actions WHERE account_id=?1 AND (updated_at>CAST(?2 AS INTEGER) OR (${live} AND expires_at>CAST(?2 AS INTEGER)))
        ORDER BY CASE WHEN ${live} THEN expires_at ELSE updated_at END DESC LIMIT ?4`).bind(accountId, since, now.toString(), maxNotices).all<StoredAction>();
    for (const row of actions.results) {
        entries.push({at: exactInt64(row.at), notice: {kind: 'external-action', actionId: row.action_id,
            status: effectiveStatus(row.status, row.expires_at, now), credentialRef: row.credential_ref, operation: row.operation,
            createdAt: apiInstant(exactInt64(row.created_at)), expiresAt: apiInstant(exactInt64(row.expires_at)),
            decidedAt: optionalInstant(row.decided_at), completedAt: optionalInstant(row.completed_at)}});
    }
    return entries.sort((a, b) => a.at > b.at ? -1 : a.at < b.at ? 1 : 0).slice(0, maxNotices).map(entry => entry.notice);
}
