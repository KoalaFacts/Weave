/** GET-only diagnostics for the existing token's own management capability.
 * No token updates, creation, rotation, deletion or alternative credentials.
 */
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';

type Env = Record<string, string | undefined>;
type Json = Record<string, unknown>;
type Report = {
    outcome: 'readable' | 'blocked'; readOnly: true; writePermissionVerified: false;
    tokenKind?: 'user' | 'account'; managementWriteDeclared?: boolean;
    stage?: string; httpStatus?: number;
};
class InspectionError extends Error {
    stage: string;
    httpStatus?: number;
    constructor(stage: string, httpStatus?: number) {
        super('Token management inspection failed safely.'); this.stage = stage; this.httpStatus = httpStatus;
    }
}
const record = (value: unknown): Json => {
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new InspectionError('invalid-metadata');
    return value as Json;
};
const tag = /^[a-f0-9]{32}$/i;

export async function inspectTokenManagement({env = process.env, fetchImpl = fetch}: {
    env?: Env; fetchImpl?: typeof fetch
} = {}): Promise<Report> {
    const boundary = {readOnly: true, writePermissionVerified: false} as const;
    try {
        const account = env.CLOUDFLARE_ACCOUNT_ID ?? '', token = env.CLOUDFLARE_API_TOKEN ?? '';
        if (!tag.test(account) || !token) throw new InspectionError('credentials');
        async function get(path: string, stage: string): Promise<unknown> {
            let response: Response;
            try {
                response = await fetchImpl('https://api.cloudflare.com/client/v4' + path, {
                    method: 'GET', redirect: 'manual', signal: AbortSignal.timeout(10000),
                    headers: {Authorization: 'Bearer ' + token, Accept: 'application/json'}
                });
            } catch { throw new InspectionError(stage); }
            if (response.status !== 200) {
                await response.body?.cancel().catch(() => {});
                throw new InspectionError(stage, response.status);
            }
            const reader = response.body?.getReader();
            if (!reader) throw new InspectionError(stage);
            try {
                const chunks: Uint8Array[] = []; let size = 0;
                for (;;) {
                    const {done, value} = await reader.read(); if (done) break;
                    size += value.length; if (size > 262144) throw new InspectionError(stage);
                    chunks.push(value);
                }
                const body = record(JSON.parse(new TextDecoder('utf-8', {fatal: true}).decode(Buffer.concat(chunks))));
                if (body.success !== true || !('result' in body)) throw new InspectionError(stage);
                return body.result;
            } catch { throw new InspectionError(stage); }
            finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
        }
        const identity = record(await get('/accounts/' + account + '/workers/subdomain', 'account-identity'));
        if (identity.subdomain !== 'weave-389') throw new InspectionError('account-identity');
        let root = '/user/tokens', tokenKind: 'user' | 'account' = 'user', verified: Json;
        try { verified = record(await get(root + '/verify', 'token-verify')); }
        catch (error) {
            if (!(error instanceof InspectionError) || ![401, 403, 404].includes(error.httpStatus ?? 0)) throw error;
            root = '/accounts/' + account + '/tokens'; tokenKind = 'account';
            verified = record(await get(root + '/verify', 'token-verify'));
        }
        if (typeof verified.id !== 'string' || !tag.test(verified.id) || verified.status !== 'active')
            throw new InspectionError('token-verify');
        const details = record(await get(root + '/' + verified.id, 'token-details'));
        if (details.id !== verified.id || details.status !== 'active' || !Array.isArray(details.policies))
            throw new InspectionError('token-details');
        const groups = await get(root + '/permission_groups', 'permission-groups');
        if (!Array.isArray(groups)) throw new InspectionError('permission-groups');
        const names = tokenKind === 'user' ? ['API Tokens Write', 'API Tokens Edit'] :
            ['Account API Tokens Write', 'Account API Tokens Edit'];
        const scope = tokenKind === 'user' ? 'com.cloudflare.api.user' : 'com.cloudflare.api.account';
        const writeIds = new Set(groups.map(record).filter(group => names.includes(String(group.name)) &&
            typeof group.id === 'string' && tag.test(group.id) &&
            Array.isArray(group.scopes) && group.scopes.includes(scope)).map(group => group.id));
        const managementWriteDeclared = details.policies.map(record).some(policy =>
            policy.effect === 'allow' && Array.isArray(policy.permission_groups) &&
            policy.permission_groups.map(record).some(group => writeIds.has(group.id)));
        // An allow declaration can still be limited by resource scope, deny policies
        // or provider rules. No write is attempted or claimed verified by this tool.
        return {outcome: 'readable', tokenKind, managementWriteDeclared, ...boundary};
    } catch (error) {
        return {outcome: 'blocked', stage: error instanceof InspectionError ? error.stage : 'invalid-metadata',
            ...(error instanceof InspectionError && error.httpStatus !== undefined ? {httpStatus: error.httpStatus} : {}), ...boundary};
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    if (process.argv.length !== 2) { console.error('Token inspection accepts no arguments.'); process.exitCode = 1; }
    else {
        const report = await inspectTokenManagement();
        console.log(JSON.stringify(report, null, 2));
        if (report.outcome !== 'readable') process.exitCode = 2;
    }
}
