/**
 * Push webhook W3: the scheduled sender of queued `app_webhook_deliveries`. Each run claims the due deliveries in one
 * transaction (counting the attempt and scheduling its retry before anything is sent, so a run that dies mid-send still
 * retries and two runs never claim the same attempt), then POSTs each body, signed with its webhook's secret, and marks the
 * ones the receiver accepted. Delivery is at least once: the receiver deduplicates on the event ID. Nothing here ever
 * reads or writes the external action itself, so a delivery failure can never change it.
 */
import { atomic, type Env } from './database.ts';
import { openWebhookSecret } from './credential-vault.ts';
import { writesRefused } from './humans.ts';
import { isoUtc } from './time.ts';

/** Attempts per delivery, the first retry delay (doubling up to the cap), the per-request timeout and deliveries per run. */
export const maxWebhookAttempts = 10, firstRetryMs = 60_000, maxRetryMs = 6 * 3600_000, webhookTimeoutMs = 5_000;
/** A run must fit the sweep's 48-statement budget: 2 reads and a batch of 3 guards plus one claim per delivery, then
 * 1 read and a batch of 3 guards plus one update per accepted delivery: 2 + 18 + 1 + 18 = 39. */
const duePerRun = 15;
const ticks = (ms: number) => BigInt(ms) * 10000n;

/** The retry delay after the `attempt`-th attempt (1-based). */
export const retryDelayMs = (attempt: number) => Math.min(firstRetryMs * 2 ** (attempt - 1), maxRetryMs);

type Due = {event_id: string; webhook_id: string; grant_id: string; action_id: string; status: string; occurred_at: string;
    attempts: number; url: string; secret_sealed: ArrayBuffer | number[]; live: number};

/** The exact bytes sent: the action and its status only, never content, credentials or tokens. */
export function webhookBody(row: {event_id: string; webhook_id: string; action_id: string; status: string; occurred_at: string}): string {
    return JSON.stringify({eventId: row.event_id, type: 'external_action.status', webhookId: row.webhook_id,
        actionId: row.action_id, status: row.status, occurredAt: isoUtc(row.occurred_at)});
}

/** `t=<unix seconds>,v1=<hex HMAC-SHA256 of "<t>.<body>">` under the webhook's secret (its UTF-8 bytes). */
export async function webhookSignature(secret: string, timestamp: number, body: string): Promise<string> {
    const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(secret), {name: 'HMAC', hash: 'SHA-256'}, false, ['sign']);
    const mac = new Uint8Array(await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(`${timestamp}.${body}`)));
    return `t=${timestamp},v1=${Array.from(mac, b => b.toString(16).padStart(2, '0')).join('')}`;
}

/** Scheduled sweep. A delivery whose webhook or grant was revoked, whose human was frozen or whose agent that human no
 * longer owns is finished unsent. */
export async function sweepWebhooks(env: Env, now: number = Date.now()): Promise<void> {
    if (writesRefused(env) || !env.EXTERNAL_CREDENTIAL_KEY) return;
    const claimed = await atomic(env.db, async tx => {
        const rows = (await tx.db.prepare(`SELECT d.event_id,d.webhook_id,w.grant_id,d.action_id,d.status,CAST(d.occurred_at AS TEXT) occurred_at,
                d.attempts,w.url,w.secret_sealed,
                (w.revoked_at IS NULL AND g.revoked_at IS NULL AND h.frozen_at IS NULL AND a.human_id IS g.human_id) live
            FROM app_webhook_deliveries d JOIN app_webhooks w ON w.webhook_id=d.webhook_id JOIN owner_grants g ON g.grant_id=w.grant_id
            JOIN humans h ON h.human_id=g.human_id LEFT JOIN external_actions x ON x.action_id=d.action_id
            LEFT JOIN accounts a ON a.account_id=x.account_id
            WHERE d.next_attempt_at IS NOT NULL AND d.next_attempt_at<=CAST(? AS INTEGER) ORDER BY d.next_attempt_at,d.event_id LIMIT ?`)
            .bind(ticks(now).toString(), duePerRun).all<Due>()).results;
        for (const row of rows) {
            const attempt = row.attempts + 1;
            const next = !row.live || attempt >= maxWebhookAttempts ? null : ticks(now + retryDelayMs(attempt)).toString();
            tx.writes.push(tx.db.prepare(`UPDATE app_webhook_deliveries SET attempts=attempts+?,next_attempt_at=CAST(? AS INTEGER)
                WHERE event_id=? AND attempts=? AND delivered_at IS NULL`).bind(row.live ? 1 : 0, next, row.event_id, row.attempts));
        }
        return rows.filter(row => row.live);
    });
    const accepted = (await Promise.all(claimed.map(async row => await send(env, row, now) ? row.event_id : null)))
        .filter((id): id is string => id !== null);
    if (!accepted.length) return;
    await atomic(env.db, async tx => {
        for (const eventId of accepted)
            tx.writes.push(tx.db.prepare(`UPDATE app_webhook_deliveries SET delivered_at=MAX(CAST(? AS INTEGER),occurred_at),next_attempt_at=NULL
                WHERE event_id=? AND delivered_at IS NULL`).bind(tx.store.now.toString(), eventId));
    });
}

/** True only for a 2xx answer within the timeout. Redirects are not followed and count as failures. */
async function send(env: Env, row: Due, now: number): Promise<boolean> {
    try {
        const secret = await openWebhookSecret(env.EXTERNAL_CREDENTIAL_KEY, row.webhook_id, row.grant_id,
            row.secret_sealed instanceof ArrayBuffer ? new Uint8Array(row.secret_sealed) : Uint8Array.from(row.secret_sealed));
        const body = webhookBody(row);
        const response = await fetch(row.url, {method: 'POST', redirect: 'manual', signal: AbortSignal.timeout(webhookTimeoutMs), body,
            headers: {'Content-Type': 'application/json', 'User-Agent': 'Weave-Webhooks/1', 'Weave-Event-Id': row.event_id,
                'Weave-Webhook-Id': row.webhook_id, 'Weave-Signature': await webhookSignature(secret, Math.floor(now / 1000), body)}});
        await response.body?.cancel();
        return response.status >= 200 && response.status < 300;
    } catch {
        console.error('app-webhook: delivery attempt failed');
        return false;
    }
}
