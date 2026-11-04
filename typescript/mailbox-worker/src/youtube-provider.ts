/**
 * YouTube through Google OAuth. Connecting exchanges an authorization code for a refresh token and reads the channel
 * that token acts for; that channel is the target account an approval binds. Endpoints are fixed, never configuration.
 * Tokens stay inside these functions: never logged, returned to a caller other than the vault, or put in an error.
 */
import type { Env } from './database.ts';

export const youtubeScopes = ['https://www.googleapis.com/auth/youtube.upload', 'https://www.googleapis.com/auth/youtube.readonly'];
const tokenEndpoint = 'https://oauth2.googleapis.com/token';
const channelsEndpoint = 'https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true';
const timeoutMs = 5_000, maxJsonBytes = 64 * 1024;

export type ConnectedAccount = {secret: string; targetAccount: string; targetDisplay: string};
export type ConnectRefusal = 'provider-refused' | 'no-channel' | 'missing-scope';

export const youtubeConfigured = (env: Env) =>
    !!env.GOOGLE_CLIENT_ID && !!env.GOOGLE_CLIENT_SECRET && !!env.EXTERNAL_CREDENTIAL_KEY;

/** A bounded JSON body under one deadline; null for a non-JSON, oversized, stalled or malformed body. */
async function boundedJson(response: Response): Promise<Record<string, unknown> | null> {
    if (!/^application\/json(?:;|$)/i.test(response.headers.get('Content-Type') ?? '')) { void response.body?.cancel().catch(() => {}); return null; }
    const reader = response.body?.getReader();
    if (!reader) return null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const deadline = new Promise<null>(resolve => { timer = setTimeout(() => resolve(null), timeoutMs); });
    const chunks: Uint8Array[] = []; let bytes = 0;
    try {
        while (true) {
            const chunk = await Promise.race([reader.read(), deadline]);
            if (chunk === null) return null;
            if (chunk.done) break;
            bytes += chunk.value.byteLength;
            if (bytes > maxJsonBytes) return null;
            chunks.push(chunk.value);
        }
        const all = new Uint8Array(bytes); let offset = 0;
        for (const chunk of chunks) { all.set(chunk, offset); offset += chunk.byteLength; }
        const value = JSON.parse(new TextDecoder('utf-8', {fatal: true}).decode(all)) as unknown;
        return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null;
    } catch { return null; }
    finally {
        if (timer !== undefined) clearTimeout(timer);
        void reader.cancel().catch(() => {});
    }
}

const send = (url: string, init: RequestInit) => fetch(url, {...init, redirect: 'manual', signal: AbortSignal.timeout(timeoutMs)});

/** Exchanges the code (PKCE) and reads the token's channel; a refusal names why, never what Google returned. */
export async function connectYoutube(env: Env, input: {code: string; verifier: string; redirectUri: string}): Promise<ConnectedAccount | ConnectRefusal> {
    // Never follow a redirect with the client secret, code or verifier.
    const exchange = await send(tokenEndpoint, {method: 'POST', headers: {'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json'},
        body: new URLSearchParams({client_id: env.GOOGLE_CLIENT_ID!, client_secret: env.GOOGLE_CLIENT_SECRET!, code: input.code,
            code_verifier: input.verifier, grant_type: 'authorization_code', redirect_uri: input.redirectUri})});
    const grant = exchange.ok ? await boundedJson(exchange) : (void exchange.body?.cancel().catch(() => {}), null);
    const accessToken = grant?.access_token, refreshToken = grant?.refresh_token, scope = grant?.scope;
    if (typeof accessToken !== 'string' || !accessToken || typeof refreshToken !== 'string' || !refreshToken) return 'provider-refused';
    // The owner may untick a scope on Google's consent screen; anything short of both is not a usable credential.
    const granted = new Set(typeof scope === 'string' ? scope.split(' ') : []);
    if (!youtubeScopes.every(required => granted.has(required))) return 'missing-scope';
    const response = await send(channelsEndpoint, {headers: {Authorization: 'Bearer ' + accessToken, Accept: 'application/json'}});
    const channels = response.ok ? await boundedJson(response) : (void response.body?.cancel().catch(() => {}), null);
    if (!channels) return 'provider-refused';
    const items = Array.isArray(channels.items) ? channels.items as unknown[] : [];
    // One channel per token: a brand account picked on the consent screen is its own channel.
    if (items.length !== 1) return 'no-channel';
    const channel = items[0] as {id?: unknown; snippet?: {title?: unknown}};
    const id = channel.id, title = channel.snippet?.title;
    if (typeof id !== 'string' || !/^UC[A-Za-z0-9_-]{22}$/.test(id) || typeof title !== 'string') return 'provider-refused';
    return {secret: refreshToken, targetAccount: id, targetDisplay: title.slice(0, 200)};
}
