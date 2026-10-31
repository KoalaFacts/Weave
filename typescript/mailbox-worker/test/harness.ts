import assert from 'node:assert/strict';
import { Miniflare, convertV4MiniflareOptions } from 'miniflare';
import { readFile } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
import { initializeDatabase } from '../scripts/latest-schema.ts';
import { agentKey, clientAssertion, dpopProof, type TestKey } from './agent-key.ts';
export const secret = () => randomBytes(32).toString('hex');
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
        const request = (path: string, control?: string | null, method = 'GET', value?: unknown) => mf.dispatchFetch('http://localhost' + path, {
            method, headers: { ...(control ? {[legacy?'X-Weave-Mailbox-Control':'X-Weave-Account-Control']:control} : {}), 'Content-Type':'application/json' },
            ...(value === undefined ? {} : { body: typeof value === 'string' ? value : JSON.stringify(value) }) });
        const ok = async (path: string, control?: string | null, method = 'GET', value?: unknown): Promise<any> => {
            const r = await request(path,control,method,value); assert.equal(r.status,200,await r.clone().text()); return r.json();
        };
        const register = async (profile = {}, control = secret()) => { const response=await ok('/v1/accounts',control,'POST',profile); return {...response,accountId:response.agentId,control}; };
        const address = (account: { control: string }, profile = {}) => ok('/v1/addresses',account.control,'POST',profile);
        const participant = async () => { const account=await register(); const card=await address(account); return {...account,cardId:card.addressId}; };
        // Ed25519 agents: register with a DPoP proof, then call with a cached DPoP-bound token and a fresh proof per request.
        const registerAgent = async (profile = {}, key: TestKey = agentKey()) => {
            const url = 'http://localhost/v1/accounts';
            const r = await mf.dispatchFetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', DPoP: dpopProof(key, { method: 'POST', url }) }, body: JSON.stringify(profile) });
            assert.equal(r.status, 200, await r.clone().text());
            const accountId = (await r.json() as { accountId: string }).accountId;
            const keyFile = JSON.stringify({ accountId, key: { kty: 'OKP', crv: 'Ed25519', x: key.privateJwk.x, d: key.privateJwk.d } });
            return { accountId, agentId: accountId, key, keyFile };
        };
        const tokens = new Map<string, string>();
        const agentRequest = async (agent: { accountId: string; key: TestKey }, path: string, method = 'GET', value?: unknown) => {
            let token = tokens.get(agent.key.kid);
            if (!token) {
                const url = 'http://localhost/v1/token';
                const form = new URLSearchParams([['grant_type', 'client_credentials'], ['client_assertion_type', 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'],
                    ['client_assertion', clientAssertion(agent.key, { accountId: agent.accountId, audience: url })]]);
                const r = await mf.dispatchFetch(url, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded', DPoP: dpopProof(agent.key, { method: 'POST', url }) }, body: form.toString() });
                assert.equal(r.status, 200, await r.clone().text());
                token = (await r.json() as { access_token: string }).access_token;
                tokens.set(agent.key.kid, token);
            }
            const url = 'http://localhost' + path;
            return mf.dispatchFetch(url, { method, headers: { Authorization: 'DPoP ' + token, DPoP: dpopProof(agent.key, { method, url, accessToken: token }), 'Content-Type': 'application/json' },
                ...(value === undefined ? {} : { body: JSON.stringify(value) }) });
        };
        const agentOk = async (agent: { accountId: string; key: TestKey }, path: string, method = 'GET', value?: unknown): Promise<any> => {
            const r = await agentRequest(agent, path, method, value); assert.equal(r.status, 200, await r.clone().text()); return r.json();
        };
        return { mf, db, request, ok, register, address, participant, registerAgent, agentRequest, agentOk, close: () => mf.dispose() };
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
