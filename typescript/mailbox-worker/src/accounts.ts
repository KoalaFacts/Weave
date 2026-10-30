import { CONTROL_HASH_SCHEME, CONTROL_HASH_PARAMETERS, controlDigest, digestMatches, validParameters } from './credentials';
import { atomic, type Env } from './database';
import { fail, hash, object, parse } from './wire';

function settings(env: Env, name: keyof Env, max: number): number {
    const text = env[name]; if (typeof text !== 'string' || !/^\d+$/.test(text)) fail(503,'storage');
    const n = Number(text); if (n < 1 || n > max) fail(503,'storage'); return n;
}
export async function authenticate(env: Env, secret: string | null): Promise<any | null> {
    if (secret === null) return null;
    const digest = (await controlDigest(secret))!;
    const row = await env.db.prepare('SELECT account_id,control_hash,control_hash_scheme,control_hash_parameters,display_name,description FROM accounts WHERE control_hash=? AND control_hash_scheme=?').bind(digest,CONTROL_HASH_SCHEME).first<any>();
    if (!row) fail(401,'unauthenticated');
    const account={accountId:row.account_id,controlHash:row.control_hash,controlHashScheme:row.control_hash_scheme,controlHashParameters:parse(row.control_hash_parameters),displayName:row.display_name,description:row.description};
    if (!validParameters(account.controlHashParameters) || !digestMatches(digest,account.controlHash)) fail(401,'unauthenticated');
    return account;
}
export async function register(env: Env, request: Request, secret: string | null, input: any): Promise<any> {
    if (!secret) fail(401,'unauthenticated'); object(input,[],['displayName','description']);
    for (const [key,max] of [['displayName',128],['description',1024]] as const) {
        if (input[key] != null && (typeof input[key] !== 'string' || input[key].length > max || /[\u0000-\u001f\u007f-\u009f]/u.test(input[key]))) fail();
    }
    const digest = (await controlDigest(secret))!;
    return atomic(env.db, async tx => {
        const existing = await tx.db.prepare('SELECT account_id FROM accounts WHERE control_hash=?').bind(digest).first<string>('account_id');
        if (existing) { const a=await tx.get('account','',existing); if (a.controlHashScheme !== CONTROL_HASH_SCHEME || !validParameters(a.controlHashParameters) || !digestMatches(digest,a.controlHash)) fail(401,'unauthenticated'); return {agentId:a.accountId,displayName:a.displayName,description:a.description}; }
        if (env.REGISTRATION_ENABLED !== 'true') fail(404,'unavailable');
        const now = Date.now(), hour = Math.floor(now / 3600000);
        const source = await hash('registration:' + hour + ':' + (request.headers.get('CF-Connecting-IP') ?? 'unknown'));
        const maxAccounts = settings(env,'REGISTRATION_MAX_ACCOUNTS',10000);
        const maxGlobal = settings(env,'REGISTRATION_GLOBAL_PER_HOUR',10000);
        const maxSource = settings(env,'REGISTRATION_PER_IP_PER_HOUR',1000);
        const count = await tx.db.prepare('SELECT count(*) n FROM accounts').first<number>('n');
        if (count === null || count >= maxAccounts) fail(429,'capacity');
        for (const [id,max] of [['global',maxGlobal],[source,maxSource]] as const) {
            const counter = await tx.get('registration-rate','',id);
            const count = counter?.hour === hour ? counter.count : 0;
            if (count >= max) fail(429,'capacity'); tx.put('registration-rate','',id,{ hour, count: count + 1 });
        }
        tx.writes.push(tx.db.prepare("DELETE FROM rate_limits WHERE seq IN (SELECT seq FROM rate_limits WHERE policy='registration' AND window_start<CAST(? AS INTEGER) LIMIT 100)").bind((BigInt(hour)*36000000000n).toString()));
        const accountId = crypto.randomUUID();
        if(await tx.get('account','',accountId))fail(409,'conflict');
        tx.put('account','',accountId,{accountId,controlHash:digest,controlHashScheme:CONTROL_HASH_SCHEME,controlHashParameters:CONTROL_HASH_PARAMETERS,displayName:input.displayName ?? null,description:input.description ?? null});
        return {agentId:accountId,displayName:input.displayName ?? null,description:input.description ?? null};
    });
}
