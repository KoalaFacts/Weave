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
    const row = await env.db.prepare("SELECT data FROM mailbox_records WHERE kind='account' AND json_extract(data,'$.controlHash')=? AND json_extract(data,'$.controlHashScheme')=?").bind(digest,CONTROL_HASH_SCHEME).first<{data:string}>();
    if (!row) fail(401,'unauthenticated');
    const account=parse(row.data);
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
        const existing = await tx.rows('account', "json_extract(data,'$.controlHash')=?", [digest]);
        if (existing.length) { const a=parse(existing[0].data); if (a.controlHashScheme !== CONTROL_HASH_SCHEME || !validParameters(a.controlHashParameters) || !digestMatches(digest,a.controlHash)) fail(401,'unauthenticated'); return {agentId:a.accountId,displayName:a.displayName,description:a.description}; }
        if (env.REGISTRATION_ENABLED !== 'true') fail(404,'unavailable');
        const now = Date.now(), hour = Math.floor(now / 3600000);
        const source = await hash('registration:' + hour + ':' + (request.headers.get('CF-Connecting-IP') ?? 'unknown'));
        const maxAccounts = settings(env,'REGISTRATION_MAX_ACCOUNTS',10000);
        const maxGlobal = settings(env,'REGISTRATION_GLOBAL_PER_HOUR',10000);
        const maxSource = settings(env,'REGISTRATION_PER_IP_PER_HOUR',1000);
        const count = await tx.db.prepare("SELECT count(*) n FROM mailbox_records WHERE kind='account'").first<number>('n');
        if (count === null || count >= maxAccounts) fail(429,'capacity');
        for (const [id,max] of [['global',maxGlobal],[source,maxSource]] as const) {
            const counter = await tx.get('registration-rate','',id);
            const count = counter?.hour === hour ? counter.count : 0;
            if (count >= max) fail(429,'capacity'); tx.put('registration-rate','',id,{ hour, count: count + 1 });
        }
        tx.writes.push(tx.db.prepare("DELETE FROM mailbox_records WHERE seq IN (SELECT seq FROM mailbox_records WHERE kind='registration-rate' AND json_extract(data,'$.hour')<? LIMIT 100)").bind(hour));
        const accountId = crypto.randomUUID();
        tx.put('account','',accountId,{accountId,controlHash:digest,controlHashScheme:CONTROL_HASH_SCHEME,controlHashParameters:CONTROL_HASH_PARAMETERS,displayName:input.displayName ?? null,description:input.description ?? null});
        return {agentId:accountId,displayName:input.displayName ?? null,description:input.description ?? null};
    });
}
