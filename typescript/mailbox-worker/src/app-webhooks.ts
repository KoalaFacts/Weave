/** Push webhook registrations (W1 step 2): the app holding an owner grant registers, lists and revokes the https callbacks
 * it wants told about external-action status changes of its human's agents. The signing secret is returned once, at
 * registration, and kept only sealed. Rows are never deleted, so every registration stays recorded. Nothing is sent yet.
 */
import { sealWebhookSecret } from './credential-vault.ts';
import { atomic, type Env, type Transaction } from './database.ts';
import { writesRefused } from './humans.ts';
import { base64url } from './jose.ts';
import { isoUtc } from './time.ts';

/** Live (unrevoked) callbacks one owner grant may hold at once. */
export const maxLiveWebhooks = 10;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export type WebhookView = {webhookId: string; url: string; createdAt: string};
export type RegisterResult = {ok: true; webhook: WebhookView; secret: string}
    | {ok: false; reason: 'invalid-url' | 'webhook-limit' | 'unavailable'};
export type RevokeResult = {ok: true} | {ok: false; reason: 'not-found' | 'unavailable'};

/** The normalized callback, or null: absolute https to a DNS name, no user info, no fragment, at most 2048 characters.
 * IP literals and localhost are refused at registration; the sender still has to check where a name resolves. */
export function webhookUrl(value: unknown): string | null {
    if (typeof value !== 'string' || value.length > 2048) return null;
    let url: URL;
    try { url = new URL(value); } catch { return null; }
    if (url.protocol !== 'https:' || url.username !== '' || url.password !== '' || url.hash !== '' || value.includes('#')) return null;
    const host = url.hostname.replace(/\.$/, '');
    if (host.startsWith('[') || /^[0-9.]+$/.test(host) || host === 'localhost' || host.endsWith('.localhost')) return null;
    return url.href.length <= 2048 ? url.href : null;
}

const view = (row: {webhook_id: string; url: string; created_at: string}): WebhookView =>
    ({webhookId: row.webhook_id, url: row.url, createdAt: isoUtc(row.created_at)});

export async function registerWebhook(env: Env, input: {grantId: string; url: unknown}, check: (tx: Transaction) => Promise<void>): Promise<RegisterResult> {
    if (writesRefused(env) || !env.EXTERNAL_CREDENTIAL_KEY) return {ok: false, reason: 'unavailable'};
    const url = webhookUrl(input.url);
    if (url === null) return {ok: false, reason: 'invalid-url'};
    const webhookId = crypto.randomUUID(), secret = base64url(crypto.getRandomValues(new Uint8Array(32)));
    const sealed = await sealWebhookSecret(env.EXTERNAL_CREDENTIAL_KEY, webhookId, input.grantId, secret);
    return atomic(env.db, async tx => {
        await check(tx);
        const live = await tx.db.prepare('SELECT COUNT(*) n FROM app_webhooks WHERE grant_id=? AND revoked_at IS NULL').bind(input.grantId).first<number>('n');
        if ((live ?? 0) >= maxLiveWebhooks) return {ok: false as const, reason: 'webhook-limit' as const};
        const at = tx.store.now.toString();
        tx.writes.push(tx.db.prepare('INSERT INTO app_webhooks(webhook_id,grant_id,url,secret_sealed,created_at) VALUES(?,?,?,?,CAST(? AS INTEGER))')
            .bind(webhookId, input.grantId, url, sealed.buffer as ArrayBuffer, at));
        return {ok: true as const, webhook: view({webhook_id: webhookId, url, created_at: at}), secret};
    });
}

/** The grant's live callbacks, oldest first; at most `maxLiveWebhooks`, so one page. */
export async function listWebhooks(env: Env, grantId: string, check: (tx: Transaction) => Promise<void>): Promise<WebhookView[]> {
    return atomic(env.db, async tx => {
        await check(tx);
        const rows = await tx.db.prepare('SELECT webhook_id,url,CAST(created_at AS TEXT) created_at FROM app_webhooks'
            + ' WHERE grant_id=? AND revoked_at IS NULL ORDER BY created_at,webhook_id LIMIT ?').bind(grantId, maxLiveWebhooks)
            .all<{webhook_id: string; url: string; created_at: string}>();
        return rows.results.map(view);
    });
}

/** Idempotent; another grant's webhook is indistinguishable from an absent one. */
export async function revokeWebhook(env: Env, input: {grantId: string; webhookId: unknown}, check: (tx: Transaction) => Promise<void>): Promise<RevokeResult> {
    if (writesRefused(env)) return {ok: false, reason: 'unavailable'};
    if (typeof input.webhookId !== 'string' || !uuidPattern.test(input.webhookId)) return {ok: false, reason: 'not-found'};
    const webhookId = input.webhookId;
    return atomic(env.db, async tx => {
        await check(tx);
        const row = await tx.db.prepare('SELECT grant_id,CAST(revoked_at AS TEXT) revoked_at FROM app_webhooks WHERE webhook_id=?')
            .bind(webhookId).first<{grant_id: string; revoked_at: string | null}>();
        if (!row || row.grant_id !== input.grantId) return {ok: false as const, reason: 'not-found' as const};
        if (row.revoked_at === null)
            tx.writes.push(tx.db.prepare('UPDATE app_webhooks SET revoked_at=CAST(? AS INTEGER) WHERE webhook_id=? AND revoked_at IS NULL')
                .bind(tx.store.now.toString(), webhookId));
        return {ok: true as const};
    });
}
