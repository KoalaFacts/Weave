/**
 * The later fate of a completed action whose effect the provider finishes after dispatch, such as a queued or scheduled
 * Postiz post. The scheduled sweep asks the provider until the state is final, with backoff and a bounded number of calls
 * per run, and never dispatches anything: tracking only reads.
 */
import { atomic, type Env, type Transaction } from './database.ts';
import { provider as findProvider, type PublicationState } from './external-providers.ts';
import { openSecret } from './credential-vault.ts';
import { apiTime, exactInt64 } from './time.ts';

export type PublicationView = {state: PublicationState['state']; url: string | null; error: string | null; checkedAt: string | null};

/** First re-check after an unfinished answer, doubling up to the cap; tracking stops this long after the effect was due. */
export const firstRecheckMs = 15 * 60_000, maxRecheckMs = 6 * 3600_000, trackingWindowMs = 7 * 24 * 3600_000;
/** Due records read per run, and credentials asked per run (Postiz allows 30 API calls per hour). */
const duePerRun = 50, credentialsPerRun = 5;
const ticks = (ms: number) => BigInt(ms) * 10000n;

/** Records a newly completed effect for tracking, in the transaction that completes the action. */
export function trackPublication(tx: Transaction, actionId: string, accountId: string, trackFrom: number): void {
    const stamp = tx.store.now.toString();
    const due = ticks(trackFrom).toString();
    tx.writes.push(tx.db.prepare(`INSERT INTO external_publications(action_id,account_id,state,due_at,checks,next_check_at,updated_at)
        VALUES(?,?,'queued',CAST(? AS INTEGER),0,CAST(? AS INTEGER),CAST(? AS INTEGER)) ON CONFLICT(action_id) DO NOTHING`)
        .bind(actionId, accountId, due, due, stamp));
}

/** The tracked state of one action, or null when it is not tracked. */
export async function readPublication(tx: Transaction, actionId: string): Promise<PublicationView | null> {
    const row = await tx.db.prepare('SELECT state,url,error,CAST(checked_at AS TEXT) checked_at FROM external_publications WHERE action_id=?')
        .bind(actionId).first<{state: PublicationState['state']; url: string | null; error: string | null; checked_at: string | null}>();
    return row && {state: row.state, url: row.url, error: row.error, checkedAt: apiTime(row.checked_at)};
}

type Due = {action_id: string; account_id: string; provider: string; credential_ref: string; receipt: string; checks: number;
    due_at: string; revision: string | null; secret: ArrayBuffer | number[] | null; revoked_at: string | null};

/** Scheduled sweep: asks providers about due records. A failure to ask only postpones; it never changes a state. */
export async function sweepPublications(env: Env, now: number = Date.now()): Promise<void> {
    if (!env.EXTERNAL_CREDENTIAL_KEY) return;
    const due = await atomic(env.db, async tx => (await tx.db.prepare(`SELECT p.action_id,p.account_id,x.provider,x.credential_ref,x.receipt,p.checks,
        CAST(p.due_at AS TEXT) due_at,c.revision,c.secret,CAST(c.revoked_at AS TEXT) revoked_at
        FROM external_publications p JOIN external_actions x ON x.action_id=p.action_id
        LEFT JOIN external_credentials c ON c.account_id=x.account_id AND c.credential_ref=x.credential_ref
        WHERE p.next_check_at IS NOT NULL AND p.next_check_at<=CAST(? AS INTEGER) ORDER BY p.next_check_at LIMIT ?`)
        .bind(ticks(now).toString(), duePerRun).all<Due>()).results);
    // One provider call per credential secret: a Postiz key covers every channel of its agent.
    const groups = new Map<string, Due[]>();
    for (const row of due) {
        const key = row.provider + '\n' + row.account_id;
        if (!groups.has(key) && groups.size >= credentialsPerRun) continue;
        groups.set(key, [...groups.get(key) ?? [], row]);
    }
    for (const rows of groups.values()) {
        try { await checkGroup(env, rows, now); }
        catch { console.error('external-publication: check failed'); }
    }
}

async function checkGroup(env: Env, rows: Due[], now: number): Promise<void> {
    const provider = findProvider(env, rows[0].provider);
    const usable = rows.find(row => row.secret !== null && row.revoked_at === null);
    const answers = new Map<string, PublicationState>();
    let asked = false;
    if (provider?.publicationStates && usable) {
        try {
            const secret = await openSecret(env.EXTERNAL_CREDENTIAL_KEY, {accountId: usable.account_id, credentialRef: usable.credential_ref,
                provider: usable.provider, revision: usable.revision!}, usable.secret instanceof ArrayBuffer ? new Uint8Array(usable.secret) : Uint8Array.from(usable.secret!));
            for (const [receipt, state] of await provider.publicationStates(secret, rows.map(row => ({receipt: row.receipt, trackFrom: Number(exactInt64(row.due_at) / 10000n)}))))
                answers.set(receipt, state);
            asked = true;
        } catch { /* postponed below */ }
    }
    await atomic(env.db, async tx => {
        const stamp = tx.store.now.toString();
        for (const row of rows) {
            const answer = answers.get(row.receipt);
            // Without a usable credential or provider the state can never be learned: stop instead of retrying forever.
            const final = answer && answer.state !== 'queued';
            const giveUp = !provider?.publicationStates || !usable || exactInt64(row.due_at) < ticks(now - trackingWindowMs);
            const delay = Math.min(firstRecheckMs * 2 ** row.checks, maxRecheckMs);
            const next = final || giveUp ? null : ticks(now + delay).toString();
            tx.writes.push(tx.db.prepare(`UPDATE external_publications SET state=?,url=?,error=?,checks=checks+?,checked_at=CASE WHEN ? THEN CAST(? AS INTEGER) ELSE checked_at END,
                next_check_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER) WHERE action_id=? AND state='queued'`)
                .bind(answer?.state ?? 'queued', answer?.url ?? null, answer?.error ?? null, asked ? 1 : 0, asked ? 1 : 0, stamp, next, stamp, row.action_id));
        }
    });
}
