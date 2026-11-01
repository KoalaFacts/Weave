import { findKey, insertKey, validLabel } from './agent-keys.ts';
import { atomic, type Env, type Transaction } from './database.ts';
import { verifyDpop } from './dpop.ts';
import { epochSeconds } from './time.ts';
import { fail, hash, object } from './wire.ts';

function settings(env: Env, name: keyof Env, max: number): number {
    const text = env[name]; if (typeof text !== 'string' || !/^\d+$/.test(text)) fail(503,'storage');
    const n = Number(text); if (n < 1 || n > max) fail(503,'storage'); return n;
}
// Shared by self-registration and human-owned agent creation: global and per-source
// hourly counters and the bounded cleanup of old counters. Total account capacity is
// a self-registration concern only.
export async function admitRegistration(env: Env, tx: Transaction, sourceIp: string): Promise<boolean> {
    const hour = Number(tx.store.now / 36000000000n);
    const source = await hash('registration:' + hour + ':' + sourceIp);
    const maxGlobal = settings(env,'REGISTRATION_GLOBAL_PER_HOUR',10000);
    const maxSource = settings(env,'REGISTRATION_PER_IP_PER_HOUR',1000);
    const increments: [string,number][] = [];
    for (const [id,max] of [['global',maxGlobal],[source,maxSource]] as const) {
        const counter = await tx.get('registration-rate','',id);
        const count = counter?.hour === hour ? counter.count : 0;
        if (count >= max) return false; increments.push([id,count + 1]);
    }
    // Nothing is staged for a refusal, so a caller may return it without committing counters.
    for (const [id,count] of increments) tx.put('registration-rate','',id,{ hour, count });
    tx.writes.push(tx.db.prepare("DELETE FROM rate_limits WHERE seq IN (SELECT seq FROM rate_limits WHERE policy='registration' AND window_start<CAST(? AS INTEGER) LIMIT 100)").bind((BigInt(hour)*36000000000n).toString()));
    return true;
}
// Self-registration gates; human-owned agents never consume REGISTRATION_MAX_ACCOUNTS.
async function admitSelfRegistration(env: Env, tx: Transaction, request: Request): Promise<void> {
    if (env.REGISTRATION_ENABLED !== 'true') fail(404,'unavailable');
    const maxAccounts = settings(env,'REGISTRATION_MAX_ACCOUNTS',10000);
    const total = await tx.db.prepare('SELECT count(*) n FROM accounts WHERE human_id IS NULL').first<number>('n');
    if (total === null || total >= maxAccounts) fail(429,'capacity');
    if (!await admitRegistration(env,tx,request.headers.get('CF-Connecting-IP') ?? 'unknown')) fail(429,'capacity');
}
function profile(input: any, optional: string[]): {displayName: string | null; description: string | null} {
    object(input,[],['displayName','description',...optional]);
    for (const [key,max] of [['displayName',128],['description',1024]] as const) {
        if (input[key] != null && (typeof input[key] !== 'string' || input[key].length > max || /[\u0000-\u001f\u007f-\u009f]/u.test(input[key]))) fail();
    }
    return {displayName:input.displayName ?? null,description:input.description ?? null};
}
async function newAccountId(tx: Transaction): Promise<string> {
    const accountId = crypto.randomUUID();
    if(await tx.get('account','',accountId))fail(409,'conflict');
    return accountId;
}
/** Self-registers an ownerless agent whose first key is the DPoP proof key; a retry with a live key returns its agent. */
export async function registerAgent(env: Env, request: Request, input: unknown): Promise<{agentId: string; displayName: string | null; description: string | null; kid: string}> {
    if (request.headers.has('Authorization')) fail();
    const proof = await verifyDpop(request,{now:epochSeconds()});
    if (!proof.ok) fail(401,'unauthenticated');
    const {displayName,description} = profile(input,['label']);
    const label = validLabel((input as {label?: unknown}).label);
    return atomic(env.db, async tx => {
        const existing = await findKey(tx.db,proof.jkt);
        if (existing) {
            if (existing.revokedAt !== null) fail(401,'unauthenticated');
            const a = await tx.get('account','',existing.accountId);
            return {agentId:a.accountId,displayName:a.displayName,description:a.description,kid:existing.kid};
        }
        if (env.RECOVERY_READ_ONLY === 'true') fail(503,'maintenance',30);
        await admitSelfRegistration(env,tx,request);
        const accountId = await newAccountId(tx);
        tx.put('account','',accountId,{accountId,humanId:null,displayName,description});
        insertKey(tx,{kid:proof.jkt,accountId,jwk:proof.jwk,label,coolingUntil:null});
        return {agentId:accountId,displayName,description,kid:proof.jkt};
    });
}
