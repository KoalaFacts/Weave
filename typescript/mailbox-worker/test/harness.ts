import assert from 'node:assert/strict';
import { Miniflare, convertV4MiniflareOptions } from 'miniflare';
import { readFile } from 'node:fs/promises';
import { randomBytes, randomUUID } from 'node:crypto';
import { initializeDatabase } from '../scripts/latest-schema.ts';
import { agentKey, clientAssertion, dpopProof, type TestKey } from './agent-key.ts';
export type AgentCredential = { accountId: string; key: TestKey };
const strangers = new WeakSet<AgentCredential>();
// Shared across start() calls so a restart on the same persisted database (for example read-only recovery, which refuses new tokens) reuses issued tokens.
const tokens = new Map<string, { accessToken: string; expiresAt: number }>();
/** The demo's saved key-file form of a registered credential. */
export const keyFile = (as: AgentCredential) => JSON.stringify({ accountId: as.accountId, key: { kty: 'OKP', crv: 'Ed25519', x: as.key.privateJwk.x, d: as.key.privateJwk.d } });
export type StartOptions = { persist?: string; migrate?: boolean; bindings?: Record<string, string>; scriptPath?: string; legacy?: boolean; outboundService?: any };
export async function start({ persist, migrate = true, bindings = {}, scriptPath = 'dist/index.js', legacy = false, outboundService }: StartOptions = {}) {
    const mf = new Miniflare(convertV4MiniflareOptions({ modules: true, scriptPath, compatibilityDate: '2026-07-01', compatibilityFlags:['nodejs_compat'],
        d1Databases: ['db'], ...(persist ? { resourcePersistencePath: persist } : {}), bindings: {
            REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100',
            REGISTRATION_PER_IP_PER_HOUR: '10', REGISTRATION_GLOBAL_PER_HOUR: '100',
            ...bindings }, ...(outboundService ? {outboundService} : {}) }));
    try {
        const db = await mf.getD1Database('db');
        if(migrate){
            if(legacy)for(const sql of (await readFile('test/fixtures/legacy-mailbox.sql','utf8')).split(';').filter(x=>x.trim()))await db.prepare(sql).run();
            else await initializeDatabase(db,BigInt(Date.now())*10000n);
        }
        const token = async (as: AgentCredential): Promise<string> => {
            if (strangers.has(as)) return randomBytes(32).toString('base64url');
            const cacheKey = as.accountId + '/' + as.key.kid, cached = tokens.get(cacheKey);
            if (cached && cached.expiresAt - Date.now() > 60000) return cached.accessToken;
            const url = 'http://localhost/v1/token';
            const form = new URLSearchParams([['grant_type', 'client_credentials'], ['client_assertion_type', 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'],
                ['client_assertion', clientAssertion(as.key, { accountId: as.accountId, audience: url })]]);
            const r = await mf.dispatchFetch(url, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded', DPoP: dpopProof(as.key, { method: 'POST', url }) }, body: form.toString() });
            assert.equal(r.status, 200, await r.clone().text());
            const issued = await r.json() as { access_token: string; expires_in: number };
            tokens.set(cacheKey, { accessToken: issued.access_token, expiresAt: Date.now() + issued.expires_in * 1000 });
            return issued.access_token;
        };
        // A DPoP-bound token plus a fresh proof for the exact URL; null sends no credential.
        const fetchAs = async (as: AgentCredential | null | undefined, url: string, init: RequestInit = {}) => {
            const headers = new Headers(init.headers);
            if (as) {
                const accessToken = await token(as);
                headers.set('Authorization', 'DPoP ' + accessToken);
                headers.set('DPoP', dpopProof(as.key, { method: init.method ?? 'GET', url, accessToken }));
            }
            return mf.dispatchFetch(url, { ...init, headers } as any);
        };
        // POST /v1/accounts registers the credential's key with a DPoP proof alone; every other path is token-authenticated.
        const request = async (path: string, as?: AgentCredential | null, method = 'GET', value?: unknown) => {
            const url = 'http://localhost' + path;
            const init = { method, headers: { 'Content-Type': 'application/json' }, ...(value === undefined ? {} : { body: typeof value === 'string' ? value : JSON.stringify(value) }) };
            if (as && method === 'POST' && path === '/v1/accounts')
                return mf.dispatchFetch(url, { ...init, headers: { ...init.headers, DPoP: dpopProof(as.key, { method, url }) } });
            return fetchAs(as, url, init);
        };
        const ok = async (path: string, as?: AgentCredential | null, method = 'GET', value?: unknown): Promise<any> => {
            const r = await request(path,as,method,value); assert.equal(r.status,200,await r.clone().text()); return r.json();
        };
        const register = async (profile = {}, key: TestKey = agentKey()): Promise<AgentCredential & { agentId: string; displayName: string | null; description: string | null; kid: string }> => {
            const response = await ok('/v1/accounts', { accountId: '', key }, 'POST', profile);
            return { ...response, key };
        };
        const stranger = (): AgentCredential => { const as = { accountId: randomUUID(), key: agentKey() }; strangers.add(as); return as; };
        const address = (account: AgentCredential, profile = {}) => ok('/v1/addresses',account,'POST',profile);
        const participant = async () => { const account=await register(); const card=await address(account); return {...account,cardId:card.addressId}; };
        return { mf, db, request, ok, fetchAs, token, register, stranger, address, participant, close: () => mf.dispose() };
    } catch (error) {
        try { await mf.dispose(); }
        catch (cleanupError) { throw new AggregateError([error, cleanupError], 'Test runtime initialization and disposal failed.', { cause: error }); }
        throw error;
    }
}
export function payload(bytes = 'hello', now = Date.now()) {
    const hex = now.toString(16).padStart(12,'0');
    return { messageId: hex.slice(0,8) + '-' + hex.slice(8) + '-7' + randomBytes(2).toString('hex').slice(1) + '-8' + randomBytes(2).toString('hex').slice(1) + '-' + randomBytes(6).toString('hex'),
        createdAt: new Date(now).toISOString(), expiresAt: new Date(now + 3600000).toISOString(), payloadEncoding:'text/plain', bytes:Buffer.from(bytes).toString('base64') };
}
