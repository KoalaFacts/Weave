import { base64url, signJws, type AgentKey } from './key.ts';

/**
 * Injection points for tests and embedding hosts; the defaults are the global fetch, the system clock and a timer. `signal` cancels
 * every request and the Retry-After wait; without it each request is cancelled after 30 seconds and the wait (at most 60 s) runs out.
 */
export type HelperOptions = {
    fetch?: typeof fetch; now?: () => number; sleep?: (milliseconds: number, signal?: AbortSignal) => Promise<void>; signal?: AbortSignal;
};
/** A DPoP-bound access token and the agent it was issued to; `expiresAt` is an ISO 8601 instant by the local clock. */
export type AccessToken = {token: string; expiresAt: string; accountId: string};

/** A refused or failed relay call. `code` is the OAuth `error` or problem `code`; the response body is not retained. */
export class WeaveHttpError extends Error {
    override name = 'WeaveHttpError';
    readonly status: number;
    readonly code: string | null;
    constructor(operation: string, status: number, code: string | null, detail = '') {
        super(`${operation} answered HTTP ${status}${code === null ? '' : ` ${code}`}${detail}`);
        this.status = status;
        this.code = code;
    }
}

/** A 429 that was not (or no longer) waited out; `retryAfter` is the relay's Retry-After in seconds. */
export class RateLimitedError extends WeaveHttpError {
    override name = 'RateLimitedError';
    readonly retryAfter: number;
    constructor(operation: string, code: string | null, retryAfter: number) {
        super(operation, 429, code, `; retry after ${retryAfter} s`);
        this.retryAfter = retryAfter;
    }
}

const assertionType = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer';
const assertionSeconds = 60;
const maxRetryWaitSeconds = 60;
// RFC 6750 b64token, bounded: a token is only ever a header value, never error text.
const b64token = /^[A-Za-z0-9\-._~+/]+=*$/;
const maxTokenLength = 4096;
const usableToken = (token: unknown): token is string => typeof token === 'string' && token.length <= maxTokenLength && b64token.test(token);
const loopback = new Set(['localhost', '127.0.0.1', '[::1]']);
// These requests authenticate with a DPoP proof alone; a 401 there is never an expired token.
const keyOnlyPaths = new Set(['/v1/accounts', '/v1/token', '/v1/key-requests']);
const encoder = new TextEncoder();
const requestTimeoutMilliseconds = 30_000;
// A relay-supplied code goes into error text: bounded, and without control or markup characters.
const safeCode = /^[A-Za-z0-9._ -]{1,64}$/;
const unsafeCode = 'unexpected-error-code';

function defaultSleep(milliseconds: number, signal?: AbortSignal): Promise<void> {
    return new Promise<void>((resolve, reject) => {
        if (signal?.aborted) return reject(signal.reason);
        const abort = () => { clearTimeout(timer); reject(signal?.reason); };
        const timer = setTimeout(() => { signal?.removeEventListener('abort', abort); resolve(); }, milliseconds);
        signal?.addEventListener('abort', abort, {once: true});
    });
}

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
    let body: unknown;
    try { body = await response.json(); } catch { return null; }
    if (typeof body !== 'object' || body === null) return null;
    const {error, code} = body as {error?: unknown; code?: unknown};
    const value = typeof error === 'string' ? error : typeof code === 'string' ? code : null;
    return value === null || safeCode.test(value) ? value : unsafeCode;
}

/** The parsed JSON body of a success response; a body that is not JSON is `unexpected-response`, and its text is never echoed. */
export async function responseJson(operation: string, response: Response): Promise<unknown> {
    try { return await response.json(); } catch (error) {
        if (error instanceof SyntaxError) throw new WeaveHttpError(operation, response.status, 'unexpected-response');
        throw error;
    }
}

async function send(options: HelperOptions | undefined, url: URL, init: RequestInit): Promise<Response> {
    // Signed requests never follow redirects: a proof is bound to the URL it names.
    return (options?.fetch ?? fetch)(url, {...init, redirect: 'manual', signal: options?.signal ?? AbortSignal.timeout(requestTimeoutMilliseconds)});
}

/** POST /v1/accounts with a DPoP proof by the key. Repeating it with a live key returns the same agent. A 401 is reported, never retried. */
export async function register(origin: string, key: AgentKey, profile: {displayName?: string} = {}, options?: HelperOptions): Promise<{accountId: string}> {
    const url = target(origin, '/v1/accounts');
    const body = profile.displayName === undefined ? {} : {displayName: profile.displayName};
    const response = await send(options, url, {method: 'POST', headers: {'Content-Type': 'application/json', DPoP: await dpopProof(key, 'POST', url, undefined, options)},
        body: JSON.stringify(body)});
    if (!response.ok) throw new WeaveHttpError('Registration', response.status, await errorCode(response));
    const result = await responseJson('Registration', response) as {accountId?: unknown; kid?: unknown} | null;
    if (typeof result !== 'object' || result === null || typeof result.accountId !== 'string' || result.kid !== key.kid) throw new WeaveHttpError('Registration', response.status, 'unexpected-response');
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
        const retryAfter = /^\d{1,6}$/.exec(response.headers.get('Retry-After') ?? '')?.[0];
        if (response.status === 429 && retryAfter !== undefined) {
            // A short wait is taken once; anything longer is the caller's decision.
            if (attempt === 0 && Number(retryAfter) <= maxRetryWaitSeconds) {
                await response.body?.cancel();
                await (options?.sleep ?? defaultSleep)(Number(retryAfter) * 1000, options?.signal);
                continue;
            }
            throw new RateLimitedError('Token request', await errorCode(response), Number(retryAfter));
        }
        if (!response.ok) throw new WeaveHttpError('Token request', response.status, await errorCode(response));
        const issued = await responseJson('Token request', response) as {access_token?: unknown; token_type?: unknown; expires_in?: unknown} | null;
        if (typeof issued !== 'object' || issued === null || !usableToken(issued.access_token) || typeof issued.token_type !== 'string' || issued.token_type.toLowerCase() !== 'dpop'
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
    if (!usableToken(token.token)) throw new TypeError('The access token is not an RFC 6750 token.');
    const verb = method.toUpperCase();
    const call = async (current: AccessToken) => send(options, url, {method: verb, body: body === undefined ? undefined : JSON.stringify(body), headers: {
        Authorization: `DPoP ${current.token}`, DPoP: await dpopProof(key, verb, url, current.token, options), ...(body === undefined ? {} : {'Content-Type': 'application/json'})}});
    const first = await call(token);
    if (first.status !== 401) return {response: first, token};
    await first.body?.cancel();
    const renewed = await accessToken(origin, key, token.accountId, options);
    return {response: await call(renewed), token: renewed};
}
