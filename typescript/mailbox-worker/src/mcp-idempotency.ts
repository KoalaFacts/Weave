/** Idempotency keys for the private MCP write tools. A key is the caller's own (per account), retained 24 h, and
 * bound to the tool and a hash of its validated input. Lookup, the business write and the key row share one atomic.
 */
import type { Transaction } from './database.ts';
import { fail, hash, invalidField } from './wire.ts';

export const IDEMPOTENCY_RETENTION_TICKS = 864000000000n;
const PRUNE_BATCH = 64, WRITE_PRUNE_BATCH = 100;
const keyPattern = /^[A-Za-z0-9._:-]{1,128}$/;

export type IdempotencyClaim = {key: string; tool: string; requestHash: string};

const canonical = (value: unknown): string => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']'
    : value !== null && typeof value === 'object'
        ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical((value as Record<string, unknown>)[key])).join(',') + '}'
        : JSON.stringify(value);

/** The claim for a supplied key over the validated input (without the key itself); null when no key was supplied. */
export async function idempotencyClaim(tool: string, key: unknown, input: Record<string, unknown>): Promise<IdempotencyClaim | null> {
    if (key === undefined) return null;
    if (typeof key !== 'string' || !keyPattern.test(key))
        invalidField('idempotencyKey', 'must be 1 to 128 characters of letters, digits, ".", "_", ":" or "-"');
    return {key, tool, requestHash: await hash(canonical({tool, input}))};
}

/** The result ID stored for a live key with the same tool and input; null for an unused or expired key; a different request conflicts. */
export async function replayedResultId(tx: Transaction, accountId: string, claim: IdempotencyClaim): Promise<string | null> {
    const row = await tx.db.prepare('SELECT tool,request_hash,result_id FROM mcp_idempotency WHERE account_id=? AND idempotency_key=? AND created_at>CAST(? AS INTEGER)')
        .bind(accountId, claim.key, (tx.store.now - IDEMPOTENCY_RETENTION_TICKS).toString()).first<{tool: string; request_hash: string; result_id: string}>();
    if (!row) return null;
    if (row.tool !== claim.tool || row.request_hash !== claim.requestHash) fail(409, 'idempotency-conflict');
    return row.result_id;
}

/** Stages the key row with the business write; an expired row for the same key is replaced. Each keyed write also prunes a bounded
 * batch of expired rows, so retention holds where the scheduled sweep is disabled. */
export function recordResultId(tx: Transaction, accountId: string, claim: IdempotencyClaim, resultId: string): void {
    tx.writes.push(tx.db.prepare(`INSERT INTO mcp_idempotency(account_id,idempotency_key,tool,request_hash,result_id,created_at) VALUES(?,?,?,?,?,CAST(? AS INTEGER))
        ON CONFLICT(account_id,idempotency_key) DO UPDATE SET tool=excluded.tool,request_hash=excluded.request_hash,result_id=excluded.result_id,created_at=excluded.created_at`)
        .bind(accountId, claim.key, claim.tool, claim.requestHash, resultId, tx.store.now.toString()));
    pruneIdempotency(tx, tx.store.now, WRITE_PRUNE_BATCH);
}

/** Stages a bounded deletion of keys older than the retention period. */
export function pruneIdempotency(tx: Transaction, now: bigint, limit = PRUNE_BATCH): void {
    tx.writes.push(tx.db.prepare(`DELETE FROM mcp_idempotency WHERE rowid IN
        (SELECT rowid FROM mcp_idempotency WHERE created_at<=CAST(? AS INTEGER) ORDER BY created_at LIMIT ?)`)
        .bind((now - IDEMPOTENCY_RETENTION_TICKS).toString(), limit));
}
