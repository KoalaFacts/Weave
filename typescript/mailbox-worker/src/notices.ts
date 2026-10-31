import { bindingColumns, bindingRow, bindingView, type StoredBinding } from './binding-requests.ts';
import { keyRequestColumns, keyRequestRow, keyRequestView, type StoredKeyRequest } from './key-requests.ts';
import { exactInt64, apiTime } from './time.ts';
import type { Transaction } from './database.ts';

export const noticeWindowTicks = 864000000000n; // 24 h
export const maxNotices = 50;

export type Notice =
    | {kind: 'key-added'; kid: string; label: string | null; createdAt: string; coolingUntil: string | null}
    | {kind: 'key-revoked'; kid: string; revokedAt: string}
    | {kind: 'key-request'; requestId: string; kid: string; endorsedBy: string; status: string; createdAt: string; expiresAt: string; decidedAt: string | null}
    | {kind: 'binding-request'; requestId: string; human: string; status: string; createdAt: string; expiresAt: string; decidedAt: string | null};

type StoredKey = {kid: string; label: string | null; created_at: string; revoked_at: string | null; cooling_until: string | null};

/**
 * Derived only from this account's own rows; nothing is stored as a message. The account's first key (its registration key) is
 * not reported as an addition. A pending request past its expiry is reported as expired, at its expiry time.
 */
export async function accountNotices(tx: Transaction, accountId: string, now: bigint): Promise<Notice[]> {
    const since = (now - noticeWindowTicks).toString();
    const entries: {at: bigint; notice: Notice}[] = [];

    const keys = await tx.db.prepare(`SELECT kid,label,CAST(created_at AS TEXT) created_at,CAST(revoked_at AS TEXT) revoked_at,CAST(cooling_until AS TEXT) cooling_until
        FROM agent_keys WHERE account_id=? AND ((revoked_at IS NULL AND created_at>CAST(? AS INTEGER)
            AND kid<>(SELECT kid FROM agent_keys WHERE account_id=? ORDER BY created_at,kid LIMIT 1)) OR revoked_at>CAST(? AS INTEGER))
        ORDER BY max(created_at,coalesce(revoked_at,0)) DESC LIMIT ?`).bind(accountId, since, accountId, since, maxNotices).all<StoredKey>();
    for (const row of keys.results) {
        if (row.revoked_at !== null) {
            const at = exactInt64(row.revoked_at);
            entries.push({at, notice: {kind: 'key-revoked', kid: row.kid, revokedAt: apiTime(at)!}});
        } else {
            const at = exactInt64(row.created_at);
            entries.push({at, notice: {kind: 'key-added', kid: row.kid, label: row.label, createdAt: apiTime(at)!,
                coolingUntil: apiTime(row.cooling_until === null ? null : exactInt64(row.cooling_until))}});
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
