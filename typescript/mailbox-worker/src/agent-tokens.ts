import * as schema from './schema.ts';
import { findKey, type KeyRow } from './agent-keys.ts';
import { admitHourly, agentLimits, agentTier } from './agent-limits.ts';
import { atomic, type Env } from './database.ts';
import { verifyDpop } from './dpop.ts';
import { base64url, parseJws, verifyJws } from './jose.ts';
import { epochSeconds, exactInt64, nowUtc } from './time.ts';
import { Problem, fail, hash } from './wire.ts';

export const accessTokenSeconds = 900;
export const assertionMaxSeconds = 300;
export type AgentAuth = {accountId: string; kid: string};

const ticksPerSecond = 10000000n;
const clockSkewSeconds = 60;
// Spent jtis outlive their exp by the skew allowance, so a Worker whose clock lags still finds them.
const assertionRetentionTicks = BigInt(clockSkewSeconds) * ticksPerSecond;
const maxFormBytes = 8192;
const expiredRowsPerIssue = 100;
const assertionType = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer';
const formParameters = new Set(['grant_type', 'client_assertion_type', 'client_assertion', 'client_id']);
const assertionJti = /^[\x20-\x7E]{16,128}$/;
const dpopAuthorization = /^DPoP ([A-Za-z0-9_-]{43})$/i;

/** An RFC 6749 §5.2 token-endpoint error; anything else thrown while issuing is reported as temporarily_unavailable. */
class TokenRefusal {
    readonly status: number;
    readonly error: string;
    readonly retryAfter: number | undefined;
    constructor(status: number, error: string, retryAfter?: number) { this.status = status; this.error = error; this.retryAfter = retryAfter; }
}
function refuse(status: number, error: string): never { throw new TokenRefusal(status, error); }
function invalidRequest(): never { refuse(400, 'invalid_request'); }
function invalidClient(): never { refuse(401, 'invalid_client'); }

function tokenResponse(status: number, value: unknown, retryAfter?: number): Response {
    return new Response(JSON.stringify(value), {status, headers: {'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'Pragma': 'no-cache',
        ...(retryAfter === undefined ? {} : {'Retry-After': String(retryAfter)})}});
}

async function readForm(request: Request): Promise<Map<string, string>> {
    if (!/^application\/x-www-form-urlencoded(?:\s*;|$)/i.test(request.headers.get('Content-Type') ?? '')) invalidRequest();
    if (Number(request.headers.get('Content-Length')) > maxFormBytes) invalidRequest();
    const reader = request.body?.getReader() ?? invalidRequest();
    const chunks: Uint8Array[] = [];
    let size = 0;
    try {
        for (let next = await reader.read(); !next.done; next = await reader.read()) {
            size += next.value.length;
            if (size > maxFormBytes) invalidRequest();
            chunks.push(next.value);
        }
    } finally { await reader.cancel(); }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    let text = '';
    try { text = new TextDecoder('utf-8', {fatal: true}).decode(bytes); } catch { invalidRequest(); }
    const form = new Map<string, string>();
    for (const [name, value] of new URLSearchParams(text)) {
        if (!formParameters.has(name) || form.has(name)) invalidRequest();
        form.set(name, value);
    }
    return form;
}

const safeInteger = (value: unknown): value is number => Number.isSafeInteger(value);

/** RFC 7523 private_key_jwt checks; every failure is the same invalid_client. */
async function assertedKey(env: Env, request: Request, assertion: string, clientId: string | undefined, now: number): Promise<{key: KeyRow; jti: string; exp: number}> {
    const jws = parseJws(assertion) ?? invalidClient();
    const kid = jws.header.kid;
    if (typeof kid !== 'string') invalidClient();
    const key = await findKey(env.db, kid);
    if (!key || key.revokedAt !== null) invalidClient();
    const {iss, sub, aud, exp, iat, nbf, jti} = jws.payload;
    if (iss !== key.accountId || sub !== key.accountId || clientId !== undefined && clientId !== key.accountId) invalidClient();
    if (!await verifyJws(jws, key.jwk)) invalidClient();
    const audience = new URL(request.url).origin + '/v1/token';
    if (aud !== audience && !(Array.isArray(aud) && aud.length === 1 && aud[0] === audience)) invalidClient();
    if (!safeInteger(exp) || !safeInteger(iat)) invalidClient();
    if (exp <= now || exp > now + assertionMaxSeconds || iat > now + clockSkewSeconds || iat < now - assertionMaxSeconds) invalidClient();
    if (nbf !== undefined && (!safeInteger(nbf) || nbf > now + clockSkewSeconds)) invalidClient();
    if (typeof jti !== 'string' || !assertionJti.test(jti)) invalidClient();
    return {key, jti, exp};
}

/** POST /v1/token: exchanges a single-use key assertion plus a DPoP proof by the same key for an opaque DPoP-bound access token. */
export async function issueAgentToken(env: Env, request: Request, clock: () => bigint = nowUtc): Promise<Response> {
    try {
        // RFC 6749 §2.3: the assertion is the only client authentication method.
        if (request.headers.has('Authorization')) invalidRequest();
        const form = await readForm(request);
        const grantType = form.get('grant_type'), type = form.get('client_assertion_type'), assertion = form.get('client_assertion');
        if (grantType === undefined || type === undefined || assertion === undefined) invalidRequest();
        if (grantType !== 'client_credentials') refuse(400, 'unsupported_grant_type');
        if (type !== assertionType) invalidRequest();
        const now = epochSeconds(clock());
        const proof = await verifyDpop(request, {now});
        if (!proof.ok) refuse(400, 'invalid_dpop_proof');
        const {key, jti, exp} = await assertedKey(env, request, assertion, form.get('client_id'), now);
        if (proof.jkt !== key.kid) refuse(400, 'invalid_dpop_proof');
        const accessToken = base64url(crypto.getRandomValues(new Uint8Array(32)));
        const tokenHash = await hash(accessToken);
        await atomic(env.db, async tx => {
            const live = await tx.db.prepare('SELECT account_id FROM agent_keys WHERE kid=? AND revoked_at IS NULL').bind(key.kid).first<string>('account_id');
            if (live !== key.accountId) invalidClient();
            const used = await tx.db.prepare('SELECT 1 used FROM agent_assertions WHERE account_id=? AND jti=?').bind(key.accountId, jti).first<number>('used');
            if (used !== null) invalidClient();
            const at = clock(), cutoff = at.toString();
            // The assertion may have expired since it was validated; its jti row could already be pruned.
            if (BigInt(exp) * ticksPerSecond <= at) invalidClient();
            // Counted only for a valid, unspent assertion, in the issuing transaction: refused or invalid attempts consume nothing.
            const admission = await admitHourly(tx, 'agent-token', key.accountId, agentLimits[await agentTier(tx, key.accountId)].tokensPerHour);
            if (!admission.admitted) throw new TokenRefusal(429, 'temporarily_unavailable', admission.retryAfter);
            // Bounded expiry cleanup first, so the rows staged below are never part of it.
            tx.writes.push(tx.db.prepare('DELETE FROM agent_assertions WHERE rowid IN (SELECT rowid FROM agent_assertions WHERE expires_at<=CAST(? AS INTEGER) LIMIT ?)')
                .bind((at - assertionRetentionTicks).toString(), expiredRowsPerIssue));
            tx.writes.push(tx.db.prepare('DELETE FROM agent_access_tokens WHERE token_hash IN (SELECT token_hash FROM agent_access_tokens WHERE expires_at<=CAST(? AS INTEGER) LIMIT ?)').bind(cutoff, expiredRowsPerIssue));
            tx.store.stage(tx.store.orm.insert(schema.agentAssertions).values({account_id: key.accountId, jti, expires_at: BigInt(exp) * ticksPerSecond, created_at: at}));
            tx.store.stage(tx.store.orm.insert(schema.agentAccessTokens).values({
                token_hash: tokenHash, account_id: key.accountId, kid: key.kid, expires_at: at + BigInt(accessTokenSeconds) * ticksPerSecond, created_at: at,
            }));
            tx.writes.push(tx.db.prepare('UPDATE agent_keys SET last_used_at=CAST(? AS INTEGER),updated_at=CAST(? AS INTEGER) WHERE kid=?').bind(cutoff, cutoff, key.kid));
        });
        return tokenResponse(200, {access_token: accessToken, token_type: 'DPoP', expires_in: accessTokenSeconds});
    } catch (error) {
        if (error instanceof TokenRefusal) return tokenResponse(error.status, {error: error.error}, error.retryAfter);
        // D1 failures fail closed: nothing was committed. Only the error class and message are logged, never request data.
        if (!(error instanceof Problem)) console.error(JSON.stringify({event: 'agent-token-unavailable', errorClass: error instanceof Error ? error.name : typeof error,
            message: error instanceof Error ? error.message : null}));
        return tokenResponse(503, {error: 'temporarily_unavailable'});
    }
}

/** Resolves `Authorization: DPoP <token>` plus its proof; null only when no Authorization header is sent. */
export async function authenticateAgent(env: Env, request: Request): Promise<AgentAuth | null> {
    const header = request.headers.get('Authorization');
    if (header === null) return null;
    const accessToken = dpopAuthorization.exec(header)?.[1] ?? fail(401, 'unauthenticated');
    const now = nowUtc();
    const proof = await verifyDpop(request, {now: epochSeconds(now), accessToken});
    if (!proof.ok) fail(401, 'unauthenticated');
    // Joining the live key makes revocation effective on the next request.
    const row = await env.db.prepare(`SELECT t.account_id,t.kid,CAST(t.expires_at AS TEXT) expires_at FROM agent_access_tokens t
        JOIN agent_keys k ON k.kid=t.kid AND k.account_id=t.account_id AND k.revoked_at IS NULL WHERE t.token_hash=?`)
        .bind(await hash(accessToken)).first<{account_id: string; kid: string; expires_at: string}>();
    if (!row || exactInt64(row.expires_at) <= now || row.kid !== proof.jkt) fail(401, 'unauthenticated');
    return {accountId: row.account_id, kid: row.kid};
}
