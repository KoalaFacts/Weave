import { bindingColumns, bindingRow, bindingView, type BindingStatus, type StoredBinding } from './binding-requests.ts';
import { keyRequestColumns, keyRequestRow, keyRequestView, type KeyRequestStatus, type StoredKeyRequest } from './key-requests.ts';
import { apiInstant, exactInt64 } from './time.ts';
import type { Transaction } from './database.ts';

export const noticeWindowTicks = 864000000000n; // 24 h
export const maxNotices = 50;

export type Notice =
    | {kind: 'key-added'; kid: string; label: string | null; createdAt: string; coolingUntil: string | null}
    | {kind: 'key-revoked'; kid: string; revokedAt: string}
    | {kind: 'key-request'; requestId: string; kid: string; endorsedBy: string; status: KeyRequestStatus; createdAt: string; expiresAt: string; decidedAt: string | null}
    | {kind: 'binding-request'; requestId: string; human: string; status: BindingStatus; createdAt: string; expiresAt: string; decidedAt: string | null};

type StoredKey = {kid: string; label: string | null; created_at: string; revoked_at: string | null; cooling_until: string | null};

/**
 * Derived only from this account's own rows; nothing is stored as a message. The registration key (inserted in the same transaction
 * as the account, so created at the account's time) is not reported as an addition; every later key is, including the first
 * recovery key of an agent created without one. A key made by confirming a key request is reported once, by that request's
 * `key-request` notice. A pending request past its expiry is reported as expired, at its expiry time.
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
    return entries.sort((a, b) => a.at > b.at ? -1 : a.at < b.at ? 1 : 0).slice(0, maxNotices).map(entry => entry.notice);
}
