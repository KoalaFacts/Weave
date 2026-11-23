/**
 * Push webhook W3: the scheduled sender of queued `app_webhook_deliveries`. Each run claims the due deliveries in one
 * transaction (counting the attempt and scheduling its retry before anything is sent, so a run that dies mid-send still
 * retries and two runs never claim the same attempt), then POSTs each body, signed with its webhook's secret, and marks the
 * ones the receiver accepted. Delivery is at least once: the receiver deduplicates on the event ID. The external action is
 * read only to check who owns its agent and is never written, so a delivery failure can never change it.
 */
import { atomic, type Env } from './database.ts';
import { budgetDatabase } from './budget.ts';
import { openWebhookSecret } from './credential-vault.ts';
import { writesRefused } from './humans.ts';
import { isoUtc } from './time.ts';

/** Attempts per delivery (the retry delay doubles from one minute, so the last one waits 256 minutes, 511 in all), the
 * per-request timeout, deliveries per run and requests in flight at once. A Worker keeps at most 6 connections open, and
 * a request's timeout starts when it is made, so no more are started at a time: one slow receiver cannot time out the
 * others while they wait for a connection. */
export const maxWebhookAttempts = 10, firstRetryMs = 60_000, webhookTimeoutMs = 5_000, concurrentWebhooks = 6;
/** Each phase runs on its own 48-statement budget. The claim costs 2 reads and a batch of 3 guards plus one claim per
 * delivery (20); marking costs 1 read and 3 guards plus one update per accepted delivery (19). A phase retried after a
 * concurrent write runs out of budget on its third try: a refused claim sends nothing, and refused marking leaves sent
 * deliveries due, to be sent again (at least once). */
const duePerRun = 15;
const ticks = (ms: number) => BigInt(ms) * 10000n;

/** The retry delay after the `attempt`-th attempt (1-based). */
export const retryDelayMs = (attempt: number) => firstRetryMs * 2 ** (attempt - 1);

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
    const claimed = await atomic(budgetDatabase(env.db), async tx => {
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
    const accepted: string[] = [];
    let cursor = 0;
    await Promise.all(Array.from({length: Math.min(concurrentWebhooks, claimed.length)}, async () => {
        while (cursor < claimed.length) {
            const row = claimed[cursor++];
            if (await send(env, row, now)) accepted.push(row.event_id);
        }
    }));
    if (!accepted.length) return;
    await atomic(budgetDatabase(env.db), async tx => {
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
