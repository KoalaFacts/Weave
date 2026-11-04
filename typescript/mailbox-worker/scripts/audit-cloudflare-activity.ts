import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// This is a fixed, read-only investigation of the existing Weave demo.
// The Cloudflare response can contain email, IPs, tokens, request/response
// bodies and activity belonging to other applications. NEVER persist it.
export const auditWindow = {
    since: '2026-10-29T12:00:00Z',
    before: '2026-11-02T02:00:00Z',
} as const;
const accountPattern = /^[0-9a-f]{32}$/i;
const uuidPattern = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const limit = 200;
const maxPages = 30;
const maxEvents = 40;

export interface AuditActivityEvidence {
    source: 'cloudflare-audit-logs-v2';
    window: typeof auditWindow;
    status: 'complete' | 'permission-denied' | 'incomplete' | 'api-error';
    httpStatus: number | null;
    pages: number;
    totalScanned: number;
    matchedD1: number;
    matchedWorker: number;
    possibleD1Mutations: number;
    sampleTruncated: boolean;
    // Whitelisted classifications only; never log actors, IPs, URIs,
    // database/account IDs, raw API responses or pagination cursors.
    events: Array<{
        time: string;
        target: 'existing-d1' | 'demo-worker';
        action: 'create' | 'update' | 'delete' | 'view' | 'other';
        method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE' | 'other';
        outcome: 'success' | 'failure' | 'other';
    }>;
    historicalRowsVerified: false;
    migrationPermitted: false;
}

type RecordValue = Record<string, unknown>;
const object = (value: unknown): RecordValue | null =>
    value !== null && typeof value === 'object' && !Array.isArray(value)
        ? value as RecordValue : null;
const allowed = <T extends string>(value: unknown, options: readonly T[], fallback: T): T =>
    typeof value === 'string' && options.includes(value as T) ? value as T : fallback;
const apiRoot = 'https://api.cloudflare.com/client/v4/accounts/';

export async function inspectCloudflareAudit(
    accountId: string, token: string, request: typeof fetch = fetch,
): Promise<AuditActivityEvidence> {
    if (!accountPattern.test(accountId) || token.length === 0)
        throw Error('Protected Cloudflare account and token are required.');
    const base = apiRoot + accountId;
    const report: AuditActivityEvidence = {
        source: 'cloudflare-audit-logs-v2', window: auditWindow,
        status: 'api-error', httpStatus: null, pages: 0, totalScanned: 0,
        matchedD1: 0, matchedWorker: 0, possibleD1Mutations: 0,
        sampleTruncated: false, events: [],
        historicalRowsVerified: false, migrationPermitted: false,
    };
    const get = async (path: string): Promise<Response> =>
        request(base + path, {
            method: 'GET', redirect: 'error',
            headers: { Authorization: 'Bearer ' + token },
            signal: AbortSignal.timeout(15000),
        });
    // Confirm the exact existing Weave account/Worker/database before
    // reading account-scoped audit records. Do not inspect other resources.
    let dbId: string;
    try {
        const subdomain = await get('/workers/subdomain');
        const subdomainBody = object(await subdomain.json());
        if (!subdomain.ok || subdomainBody?.success !== true
            || object(subdomainBody.result)?.subdomain !== 'weave-389')
            return report;
        const settings = await get('/workers/scripts/demo/settings');
        const settingsBody = object(await settings.json());
        const bindings = object(settingsBody?.result)?.bindings;
        const candidates = Array.isArray(bindings) ? bindings.map(object).filter(x => x?.name === 'db') : [];
        const candidate = candidates.length === 1 ? candidates[0] : null;
        if (!settings.ok || settingsBody?.success !== true || candidate?.type !== 'd1'
            || typeof candidate.id !== 'string' || !uuidPattern.test(candidate.id))
            return report;
        dbId = candidate.id;
        const database = await get('/d1/database/' + dbId);
        const dbBody = object(await database.json());
        if (!database.ok || dbBody?.success !== true || object(dbBody.result)?.uuid !== dbId
            || object(dbBody.result)?.name !== 'weave-demo-db')
            return report;
    } catch {
        return report;
    }
    const prefix = '/accounts/' + accountId;
    const d1Prefix = prefix + '/d1/database/' + dbId;
    const workerPrefix = prefix + '/workers/scripts/demo';
    const seenCursors = new Set<string>();
    let cursor: string | null = null;
    for (let page = 0; page < maxPages; page++) {
        const url = new URL(base + '/logs/audit');
        url.searchParams.set('since', auditWindow.since);
        url.searchParams.set('before', auditWindow.before);
        url.searchParams.set('direction', 'asc');
        url.searchParams.set('limit', String(limit));
        if (cursor !== null) url.searchParams.set('cursor', cursor);
        let response: Response;
        try {
            // The URL is assembled locally. No user-supplied host or redirect.
            response = await request(url.toString(), {
                method: 'GET', redirect: 'error',
                headers: { Authorization: 'Bearer ' + token },
                signal: AbortSignal.timeout(15000),
            });
        } catch {
            return report;
        }
        report.httpStatus = response.status;
        if (response.status === 401 || response.status === 403) {
            report.status = 'permission-denied';
            return report;
        }
        if (!response.ok) return report;
        let body: RecordValue | null;
        try {
            const raw = await response.text();
            if (raw.length > 4_000_000) return report;
            body = object(JSON.parse(raw) as unknown);
        } catch {
            return report;
        }
        if (body?.success !== true || !Array.isArray(body.result)) return report;
        report.pages++;
        for (const value of body.result) {
            report.totalScanned++;
            const entry = object(value);
            const raw = object(entry?.raw);
            const resource = object(entry?.resource);
            // Match only the immutable D1 UUID or exact deployed demo Worker.
            // Never output URI or resource IDs, even for matching events.
            const uri = typeof raw?.uri === 'string' ? raw.uri.split('?')[0] : '';
            const normalized = uri.startsWith('/client/v4/') ? uri.slice('/client/v4'.length) : uri;
            const isD1 = normalized === d1Prefix || normalized.startsWith(d1Prefix + '/')
                || (normalized === '' && resource?.id === dbId && typeof resource?.product === 'string' && /d1/i.test(resource.product));
            const isWorker = normalized === workerPrefix || normalized.startsWith(workerPrefix + '/')
                || (normalized === '' && resource?.id === 'demo' && typeof resource?.product === 'string' && /worker/i.test(resource.product));
            if (!isD1 && !isWorker) continue;
            const action = object(entry?.action);
            const time = action?.time;
            const timeMs = typeof time === 'string' ? Date.parse(time) : NaN;
            if (!Number.isFinite(timeMs) || timeMs < Date.parse(auditWindow.since)
                || timeMs >= Date.parse(auditWindow.before))
                continue;
            const method = allowed(raw?.method, ['GET','POST','PUT','PATCH','DELETE','other'] as const, 'other');
            const kind = allowed(action?.type, ['create','update','delete','view','other'] as const, 'other');
            const outcome = allowed(action?.result, ['success','failure','other'] as const, 'other');
            if (isD1) {
                report.matchedD1++;
                // POST /query may be read-only. This is a candidate, NOT
                // evidence of a destructive statement or data disappearance.
                if (method !== 'GET' && method !== 'other') report.possibleD1Mutations++;
            } else report.matchedWorker++;
            if (report.events.length < maxEvents) {
                report.events.push({
                    time: new Date(timeMs).toISOString(), target: isD1 ? 'existing-d1' : 'demo-worker',
                    action: kind, method, outcome,
                });
            } else report.sampleTruncated = true;
        }
        const resultInfo = object(body.result_info);
        const next = resultInfo?.cursor;
        if (next === null || next === undefined || next === '') {
            report.status = 'complete';
            return report;
        }
        if (typeof next !== 'string' || next.length > 2048 || seenCursors.has(next))
            return {...report,status:'incomplete'};
        seenCursors.add(next);
        cursor = next;
    }
    return {...report,status:'incomplete'};
}

async function main() {
    const accountId = process.env.CLOUDFLARE_ACCOUNT_ID ?? '';
    const token = process.env.CLOUDFLARE_API_TOKEN ?? '';
    const evidence = await inspectCloudflareAudit(accountId, token);
    const directory = join(dirname(fileURLToPath(import.meta.url)), '..', 'test-results', 'read-only-d1');
    await mkdir(directory, {recursive:true});
    await writeFile(join(directory, 'cloudflare-activity.json'), JSON.stringify(evidence,null,2) + '\n', {mode:0o600});
    console.log('Weave Cloudflare audit: ' + evidence.status + ', HTTP '
        + (evidence.httpStatus ?? 'unknown') + ', pages ' + evidence.pages
        + ', relevant D1/Worker events ' + (evidence.matchedD1 + evidence.matchedWorker));
    if (evidence.status !== 'complete') process.exitCode = 2;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1])
    main().catch(() => {
        // Never print failed response bodies, request headers or secret values.
        console.error('Read-only Cloudflare audit could not complete.');
        process.exitCode = 2;
    });
