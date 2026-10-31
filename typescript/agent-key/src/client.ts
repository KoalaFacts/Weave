import { base64url, signJws, type AgentKey } from './key.ts';

/** Injection points for tests and embedding hosts; the defaults are the global fetch, the system clock and a timer. */
export type HelperOptions = {fetch?: typeof fetch; now?: () => number; sleep?: (milliseconds: number) => Promise<void>};
/** A DPoP-bound access token and the agent it was issued to; `expiresAt` is an ISO 8601 instant by the local clock. */
export type AccessToken = {token: string; expiresAt: string; accountId: string};

/** A refused or failed relay call. `code` is the OAuth `error` or problem `code`; the response body is not retained. */
export class WeaveHttpError extends Error {
    override name = 'WeaveHttpError';
    readonly status: number;
    readonly code: string | null;
    constructor(operation: string, status: number, code: string | null) {
        super(`${operation} answered HTTP ${status}${code === null ? '' : ` ${code}`}`);
        this.status = status;
        this.code = code;
    }
}

const assertionType = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer';
const assertionSeconds = 60;
const loopback = new Set(['localhost', '127.0.0.1', '[::1]']);
// These requests authenticate with a DPoP proof alone; a 401 there is never an expired token.
const keyOnlyPaths = new Set(['/v1/accounts', '/v1/token', '/v1/key-requests']);
const encoder = new TextEncoder();
const defaultSleep = (milliseconds: number) => new Promise<void>(resolve => setTimeout(resolve, milliseconds));

/** The service origin: HTTPS, or HTTP on loopback only, with no path, query, fragment or credentials. */
export function serviceOrigin(origin: string): string {
    let url: URL;
    try { url = new URL(origin); } catch { throw new TypeError('The origin is not a URL.'); }
    const secure = url.protocol === 'https:' || url.protocol === 'http:' && loopback.has(url.hostname);
    if (!secure || url.username || url.password || url.pathname !== '/' || url.search || url.hash)
        throw new TypeError('The origin must be an https:// origin (http:// only on loopback) without path, query or credentials.');
    return url.origin;
}

function target(origin: string, path: string): URL {
    const base = serviceOrigin(origin);
    if (!path.startsWith('/') || path.startsWith('//')) throw new TypeError('The path must start with a single "/".');
    const url = new URL(base + path);
    if (url.origin !== base) throw new TypeError('The path must stay on the service origin.');
    return url;
}

const seconds = (options: HelperOptions | undefined) => Math.floor((options?.now ?? Date.now)() / 1000);

/** An RFC 9449 proof: `htu` is the URL without query or fragment, `htm` the uppercase method, `ath` the token hash when one is sent. */
export async function dpopProof(key: AgentKey, method: string, url: URL, accessToken?: string, options?: HelperOptions): Promise<string> {
    const payload: Record<string, unknown> = {jti: crypto.randomUUID(), htm: method.toUpperCase(), htu: url.origin + url.pathname, iat: seconds(options)};
    if (accessToken !== undefined) payload.ath = base64url(new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(accessToken))));
    return signJws(key, {typ: 'dpop+jwt', jwk: key.publicJwk}, payload);
}

async function errorCode(response: Response): Promise<string | null> {
    try {
        const body: unknown = await response.json();
        if (typeof body !== 'object' || body === null) return null;
        const {error, code} = body as {error?: unknown; code?: unknown};
        return typeof error === 'string' ? error : typeof code === 'string' ? code : null;
    } catch { return null; }
}

async function send(options: HelperOptions | undefined, url: URL, init: RequestInit): Promise<Response> {
    // Signed requests never follow redirects: a proof is bound to the URL it names.
    return (options?.fetch ?? fetch)(url, {...init, redirect: 'manual'});
}

/** POST /v1/accounts with a DPoP proof by the key. Repeating it with a live key returns the same agent. A 401 is reported, never retried. */
export async function register(origin: string, key: AgentKey, profile: {displayName?: string} = {}, options?: HelperOptions): Promise<{accountId: string}> {
    const url = target(origin, '/v1/accounts');
    const body = profile.displayName === undefined ? {} : {displayName: profile.displayName};
    const response = await send(options, url, {method: 'POST', headers: {'Content-Type': 'application/json', DPoP: await dpopProof(key, 'POST', url, undefined, options)},
        body: JSON.stringify(body)});
    if (!response.ok) throw new WeaveHttpError('Registration', response.status, await errorCode(response));
    const result = await response.json() as {accountId?: unknown; kid?: unknown};
    if (typeof result.accountId !== 'string' || result.kid !== key.kid) throw new WeaveHttpError('Registration', response.status, 'unexpected-response');
    return {accountId: result.accountId};
}

/** POST /v1/token with a single-use `private_key_jwt` assertion and a DPoP proof; a 429 waits for `Retry-After` once. */
export async function accessToken(origin: string, key: AgentKey, accountId: string, options?: HelperOptions): Promise<AccessToken> {
    const url = target(origin, '/v1/token');
    const audience = url.origin + url.pathname;
    for (let attempt = 0; ; attempt++) {
        const iat = seconds(options);
        const assertion = await signJws(key, {kid: key.kid}, {iss: accountId, sub: accountId, aud: audience, iat, exp: iat + assertionSeconds, jti: crypto.randomUUID()});
        const form = new URLSearchParams([['grant_type', 'client_credentials'], ['client_assertion_type', assertionType], ['client_assertion', assertion], ['client_id', accountId]]);
        const response = await send(options, url, {method: 'POST', headers: {'Content-Type': 'application/x-www-form-urlencoded', DPoP: await dpopProof(key, 'POST', url, undefined, options)},
            body: form.toString()});
        if (response.status === 429 && attempt === 0) {
            const retryAfter = response.headers.get('Retry-After');
            if (retryAfter !== null && /^\d{1,6}$/.test(retryAfter)) {
                await response.body?.cancel();
                await (options?.sleep ?? defaultSleep)(Number(retryAfter) * 1000);
                continue;
            }
        }
        if (!response.ok) throw new WeaveHttpError('Token request', response.status, await errorCode(response));
        const issued = await response.json() as {access_token?: unknown; token_type?: unknown; expires_in?: unknown};
        if (typeof issued.access_token !== 'string' || typeof issued.token_type !== 'string' || issued.token_type.toLowerCase() !== 'dpop'
            || typeof issued.expires_in !== 'number' || !(issued.expires_in > 0))
            throw new WeaveHttpError('Token request', response.status, 'unexpected-response');
        return {token: issued.access_token, expiresAt: new Date(iat * 1000 + issued.expires_in * 1000).toISOString(), accountId};
    }
}

/**
 * A token-authenticated request with `Authorization: DPoP <token>` and a fresh proof carrying `ath`. On a 401 it obtains one new
 * token and repeats the request once; the token in the result is the one to keep using.
 */
export async function fetchWithKey(origin: string, key: AgentKey, token: AccessToken, method: string, path: string, body?: unknown,
    options?: HelperOptions): Promise<{response: Response; token: AccessToken}> {
    const url = target(origin, path);
    if (keyOnlyPaths.has(url.pathname)) throw new TypeError(`${url.pathname} is not a token-authenticated request.`);
    const verb = method.toUpperCase();
    const call = async (current: AccessToken) => send(options, url, {method: verb, body: body === undefined ? undefined : JSON.stringify(body), headers: {
        Authorization: `DPoP ${current.token}`, DPoP: await dpopProof(key, verb, url, current.token, options), ...(body === undefined ? {} : {'Content-Type': 'application/json'})}});
    const first = await call(token);
    if (first.status !== 401) return {response: first, token};
    await first.body?.cancel();
    const renewed = await accessToken(origin, key, token.accountId, options);
    return {response: await call(renewed), token: renewed};
}
