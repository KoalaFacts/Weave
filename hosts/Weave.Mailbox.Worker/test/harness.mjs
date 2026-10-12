import assert from 'node:assert/strict';
import { Miniflare, convertV4MiniflareOptions } from 'miniflare';
import { readFile } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
export const secret = () => randomBytes(32).toString('hex');
export async function start({ persist, migrate = true, bindings = {} } = {}) {
    const mf = new Miniflare(convertV4MiniflareOptions({ modules: true, scriptPath: 'dist/index.js', compatibilityDate: '2026-07-01', compatibilityFlags:['nodejs_compat'],
        d1Databases: ['db'], ...(persist ? { resourcePersistencePath: persist } : {}), bindings: {
            REGISTRATION_ENABLED: 'true', REGISTRATION_MAX_ACCOUNTS: '100',
            REGISTRATION_PER_IP_PER_HOUR: '10', REGISTRATION_GLOBAL_PER_HOUR: '100',
            ...bindings } }));
    const db = await mf.getD1Database('db');
    if (migrate) for (const sql of (await readFile('migrations/0001_mailbox.sql','utf8')).split(';').filter(x => x.trim())) await db.prepare(sql).run();
    const request = (path, control, method = 'GET', value) => mf.dispatchFetch('http://localhost' + path, {
        method, headers: { ...(control ? {'X-Weave-Mailbox-Control':control} : {}), 'Content-Type':'application/json' },
        ...(value === undefined ? {} : { body: typeof value === 'string' ? value : JSON.stringify(value) }) });
    const ok = async (path, control, method = 'GET', value) => {
        const r = await request(path,control,method,value); assert.equal(r.status,200,await r.clone().text()); return r.json();
    };
    const register = async (profile = {}, control = secret()) => { const response=await ok('/v1/accounts',control,'POST',profile); return {...response,accountId:response.agentId,control}; };
    const address = (account, profile = {}) => ok('/v1/addresses',account.control,'POST',profile);
    const participant = async () => { const account=await register(); const card=await address(account); return {...account,cardId:card.addressId}; };
    return { mf, db, request, ok, register, address, participant, close: () => mf.dispose() };
}
export function payload(bytes = 'hello', now = Date.now()) {
    const hex = now.toString(16).padStart(12,'0');
    return { messageId: hex.slice(0,8) + '-' + hex.slice(8) + '-7' + randomBytes(2).toString('hex').slice(1) + '-8' + randomBytes(2).toString('hex').slice(1) + '-' + randomBytes(6).toString('hex'),
        createdAt: new Date(now).toISOString(), expiresAt: new Date(now + 3600000).toISOString(), payloadEncoding:'text/plain', bytes:Buffer.from(bytes).toString('base64') };
}
