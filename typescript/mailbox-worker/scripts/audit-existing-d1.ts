import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import type { StorageDatabase } from './latest-schema.ts';
import { completedMigration } from './completed-migration.ts';

// This inventory deliberately cannot migrate, deploy, reset or repair D1.
// No row identifiers, tokens, contents, subjects or email addresses are read.
export const inventoryTables = [
    'accounts', 'humans', 'agent_keys', 'account_addresses', 'contact_cards',
    'contact_requests', 'contacts', 'mailbox_records', 'message_delivery',
    'client_grants', 'mcp_idempotency', 'oauth_identity_links',
    'mailbox_records_legacy', 'data_migrations',
    // The original core-only view missed pending agent auth/binding state,
    // legacy channel/cursor state and the D1 migration ledger. Count rows only.
    'agent_assertions', 'agent_access_tokens', 'binding_requests',
    'key_requests', 'recovery_codes', 'address_agent_cards',
    'legacy_channels', 'rate_limits', 'stream_leases', 'stream_cursors',
    'counters', 'maintenance_state', 'mailbox_revision', 'mailbox_guard',
    'd1_migrations',
] as const;
const requiredTables = [
    'accounts', 'account_addresses', 'contact_requests', 'contacts',
    'mailbox_records', 'message_delivery',
] as const;
const currentAccountColumns = [
    'account_id', 'human_id', 'display_name', 'description', 'created_at', 'updated_at',
];

type AuditCount = string | null;
export interface ReadOnlyD1Audit {
    status: 'observed' | 'needs-review';
    schema: 'six-column-human-linked' | 'other-review-required';
    rowCounts: Record<string, AuditCount>;
    accountColumns: string[];
    acknowledged: AuditCount;
    humanLinkedAccounts: AuditCount;
    normalizeMailboxLedger: {
        state: 'complete' | 'missing' | 'unavailable' | 'ambiguous' | 'incomplete' | 'unexpected-checksum';
        completedAtTicks: string | null;
    };
    orphanCounts: Record<string, AuditCount>;
    missingRequiredTables: string[];
    untrackedTableCount: number;
    findings: string[];
    // A present-day count alone never proves historical record preservation.
    historicalPreservationProven: false;
    migrationPermitted: false;
}

export async function auditExistingD1(db: StorageDatabase): Promise<ReadOnlyD1Audit> {
    const entries = (await db.prepare("SELECT name FROM sqlite_master WHERE type='table'").all<{name:string}>()).results;
    const present = new Set(entries.map(row => row.name));
    const columns = new Map<string, Set<string>>();
    const rowCounts: Record<string, AuditCount> = {};
    const missingRequiredTables = requiredTables.filter(name => !present.has(name));
    const findings: string[] = missingRequiredTables.length ? ['missing-core-tables'] : [];
    // This table is unmanaged in today's Drizzle schema but existed in the
    // reviewed pre-agent-key source. Absence is not proof it contained no data.
    if (!present.has('oauth_identity_links')) findings.push('legacy-oauth-identity-history-unverified');
    async function count(sql: string): Promise<string> {
        const row = await db.prepare(sql).first<{value:string}>();
        if (row === null || !/^\d+$/.test(String(row.value))) throw Error('Invalid aggregate count from D1.');
        return String(row.value);
    }
    for (const name of inventoryTables) {
        if (!present.has(name)) {
            rowCounts[name] = null;
            continue;
        }
        // Identifiers are a fixed in-source allowlist, never supplied by a caller.
        const fields = (await db.prepare('PRAGMA table_info("' + name + '")').all<{name:string}>()).results.map(row => row.name);
        columns.set(name, new Set(fields));
        rowCounts[name] = await count('SELECT CAST(COUNT(*) AS TEXT) AS value FROM "' + name + '"');
    }
    const accountColumns = [...(columns.get('accounts') ?? [])];
    const current = accountColumns.length === currentAccountColumns.length
        && accountColumns.every((name, index) => name === currentAccountColumns[index]);
    if (!current) findings.push('account-schema-requires-review');
    const has = (table:string, ...fields:string[]) =>
        present.has(table) && fields.every(field => columns.get(table)?.has(field));
    let ledgerState: ReadOnlyD1Audit['normalizeMailboxLedger']['state'] = 'unavailable';
    let completedAtTicks: string | null = null;
    if (has('data_migrations', 'script_id', 'checksum', 'completed_at')) {
        // A fixed allowlisted read: compare the stored checksum without returning its raw value.
        const rows = (await db.prepare(
            'SELECT CASE WHEN checksum=? THEN 1 ELSE 0 END AS checksum_matches, '
            + 'CAST(completed_at AS TEXT) AS completed_at FROM data_migrations WHERE script_id=? LIMIT 2'
        ).bind(completedMigration.checksum, completedMigration.scriptId)
            .all<{checksum_matches:number;completed_at:string|null}>()).results;
        if (rows.length === 0) ledgerState = 'missing';
        else if (rows.length !== 1) ledgerState = 'ambiguous';
        else {
            const row = rows[0];
            if (typeof row.completed_at === 'string' && /^[0-9]+$/.test(row.completed_at))
                completedAtTicks = row.completed_at;
            ledgerState = row.checksum_matches !== 1 ? 'unexpected-checksum'
                : completedAtTicks === null ? 'incomplete' : 'complete';
        }
    }
    if (ledgerState !== 'complete') findings.push('normalize-mailbox-ledger-' + ledgerState);
    const normalizeMailboxLedger = {state:ledgerState, completedAtTicks};

    const orphans: Record<string, AuditCount> = {};
    const relationships = [
        ['messages-sender', 'mailbox_records', ['sender_account_id'], 'accounts', 'account_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM mailbox_records m LEFT JOIN accounts a ON a.account_id=m.sender_account_id WHERE a.account_id IS NULL'],
        ['messages-recipient', 'mailbox_records', ['recipient_account_id'], 'accounts', 'account_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM mailbox_records m LEFT JOIN accounts a ON a.account_id=m.recipient_account_id WHERE a.account_id IS NULL'],
        ['delivery-message', 'message_delivery', ['message_seq'], 'mailbox_records', 'seq',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM message_delivery d LEFT JOIN mailbox_records m ON m.seq=d.message_seq WHERE m.seq IS NULL'],
        ['contacts-owner', 'contacts', ['owner_account_id'], 'accounts', 'account_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM contacts c LEFT JOIN accounts a ON a.account_id=c.owner_account_id WHERE a.account_id IS NULL'],
        ['contacts-peer', 'contacts', ['to_account_id'], 'accounts', 'account_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM contacts c LEFT JOIN accounts a ON a.account_id=c.to_account_id WHERE a.account_id IS NULL'],
        ['requests-sender', 'contact_requests', ['requester_account_id'], 'accounts', 'account_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM contact_requests r LEFT JOIN accounts a ON a.account_id=r.requester_account_id WHERE a.account_id IS NULL'],
        ['requests-recipient', 'contact_requests', ['recipient_account_id'], 'accounts', 'account_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM contact_requests r LEFT JOIN accounts a ON a.account_id=r.recipient_account_id WHERE a.account_id IS NULL'],
        ['grants-account', 'client_grants', ['account_id'], 'accounts', 'account_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM client_grants g LEFT JOIN accounts a ON a.account_id=g.account_id WHERE a.account_id IS NULL'],
        ['grants-human', 'client_grants', ['human_id'], 'humans', 'human_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM client_grants g LEFT JOIN humans h ON h.human_id=g.human_id WHERE h.human_id IS NULL'],
        ['legacy-oauth-links-account', 'oauth_identity_links', ['account_id'], 'accounts', 'account_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM oauth_identity_links l LEFT JOIN accounts a ON a.account_id=l.account_id WHERE a.account_id IS NULL'],
        ['accounts-human', 'accounts', ['human_id'], 'humans', 'human_id',
            'SELECT CAST(COUNT(*) AS TEXT) AS value FROM accounts a LEFT JOIN humans h ON h.human_id=a.human_id WHERE a.human_id IS NOT NULL AND h.human_id IS NULL'],
    ] as const;
    for (const [key, source, fields, target, targetField, query] of relationships) {
        const possible = has(source, ...fields) && has(target, targetField);
        orphans[key] = possible ? await count(query) : null;
        if (orphans[key] === null) findings.push('unchecked-reference-' + key);
        else if (orphans[key] !== '0') findings.push('orphaned-reference-' + key);
    }
    const acknowledged = has('message_delivery', 'state')
        ? await count("SELECT CAST(COUNT(*) AS TEXT) AS value FROM message_delivery WHERE state='acknowledged'")
        : null;
    const humanLinkedAccounts = has('accounts', 'human_id')
        ? await count('SELECT CAST(COUNT(*) AS TEXT) AS value FROM accounts WHERE human_id IS NOT NULL')
        : null;
    const known = new Set<string>(inventoryTables);
    return {
        status: findings.length ? 'needs-review' : 'observed',
        schema: current ? 'six-column-human-linked' : 'other-review-required',
        rowCounts, accountColumns, acknowledged, humanLinkedAccounts, normalizeMailboxLedger,
        orphanCounts: orphans, missingRequiredTables,
        untrackedTableCount: entries.filter(row => !known.has(row.name) && !row.name.startsWith('sqlite_')).length,
        findings, historicalPreservationProven: false, migrationPermitted: false,
    };
}

// Cloudflare Time Travel's read-only bookmark endpoint cannot read historical
// rows or prove that any record was retained. Never call the restore endpoint.
export const historicalBookmarkTimes = [
    {label:'2026-10-17-schema-observation', at:'2026-10-17T12:00:00.000Z'},
    {label:'2026-11-01-before-ledger-change', at:'2026-11-01T23:57:00.000Z'},
    {label:'2026-11-02-after-ledger-change', at:'2026-11-02T00:05:00.000Z'},
] as const;

export interface TimeTravelBookmarkObservation {
    label:string;
    requestedAt:string|null;
    status:'bookmark-returned'|'no-bookmark'|'permission-denied'|'not-available'|'api-error';
    httpStatus:number|null;
    // A digest permits equality comparison without publishing a restoration bookmark.
    bookmarkSha256:string|null;
}

export async function inspectTimeTravelBookmarks(
    url:string, token:string, request:typeof fetch = fetch,
): Promise<TimeTravelBookmarkObservation[]> {
    const samples: {label:string;at:string|null}[] = [
        {label:'current',at:null},
        ...historicalBookmarkTimes,
    ];
    const observed:TimeTravelBookmarkObservation[] = [];
    for (const {label,at} of samples) {
        const endpoint = at === null ? url : url + '?timestamp=' + encodeURIComponent(at);
        let response:Response;
        try {
            response = await request(endpoint, {
                method:'GET', headers:{Authorization:'Bearer ' + token},
                redirect:'error', signal:AbortSignal.timeout(10000),
            });
        } catch {
            observed.push({label,requestedAt:at,status:'api-error',httpStatus:null,bookmarkSha256:null});
            continue;
        }
        const base = {label,requestedAt:at,httpStatus:response.status,bookmarkSha256:null};
        if (!response.ok) {
            const status:TimeTravelBookmarkObservation['status'] = response.status === 401 || response.status === 403 ? 'permission-denied'
                : response.status === 404 || response.status === 410 ? 'not-available' : 'api-error';
            observed.push({...base,status});
            continue;
        }
        try {
            const body = await response.json() as {success?:boolean;result?:{bookmark?:unknown}};
            const bookmark = body.success === true ? body.result?.bookmark : null;
            if (typeof bookmark !== 'string' || bookmark.length === 0) {
                observed.push({...base,status:'no-bookmark'});
                continue;
            }
            observed.push({
                ...base,status:'bookmark-returned',
                bookmarkSha256:createHash('sha256').update(bookmark).digest('hex'),
            });
        } catch {
            observed.push({...base,status:'api-error'});
        }
    }
    return observed;
}

interface CloudflareResult<T> {success: boolean;result:T;}
interface Binding {name:string;type:string;id?:string;}
const workerRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export async function runRemoteInventory(env: NodeJS.ProcessEnv = process.env) {
    if (!/^[a-f0-9]{32}$/i.test(env.CLOUDFLARE_ACCOUNT_ID ?? '') || !env.CLOUDFLARE_API_TOKEN)
        throw Error('Protected Cloudflare account and existing token are required.');
    const base = JSON.parse(await readFile(join(workerRoot, 'wrangler.jsonc'), 'utf8')) as Record<string, unknown>;
    if (base.name !== 'demo') throw Error('Unexpected Worker configuration; refusing to inspect another target.');
    const api = 'https://api.cloudflare.com/client/v4/accounts/' + env.CLOUDFLARE_ACCOUNT_ID;
    async function get<T>(path:string):Promise<T> {
        const response = await fetch(api + path, {
            headers: {Authorization:'Bearer ' + env.CLOUDFLARE_API_TOKEN},
            redirect:'error', signal:AbortSignal.timeout(10000),
        });
        if (!response.ok) throw Error('Cloudflare read-only resource verification failed (HTTP ' + response.status + ').');
        const body = await response.json() as CloudflareResult<T>;
        if (body?.success !== true || !body.result) throw Error('Unexpected Cloudflare resource response.');
        return body.result;
    }
    const subdomain = await get<{subdomain:string}>('/workers/subdomain');
    if (subdomain.subdomain !== 'weave-389') throw Error('Unexpected account subdomain.');
    const settings = await get<{bindings:Binding[]}>('/workers/scripts/demo/settings');
    const dbBindings = settings.bindings?.filter(binding => binding.name === 'db') ?? [];
    if (dbBindings.length !== 1 || dbBindings[0].type !== 'd1'
        || !/^[a-f0-9-]{36}$/i.test(dbBindings[0].id ?? ''))
        throw Error('No unique verified live demo D1 binding.');
    const database = await get<{uuid:string;name:string}>('/d1/database/' + dbBindings[0].id);
    if (database.uuid !== dbBindings[0].id || !/^[A-Za-z0-9_-]+$/.test(database.name))
        throw Error('Verified live D1 identity does not match Worker binding.');
    const temporary = await mkdtemp(join(tmpdir(), 'weave-d1-readonly-'));
    let dispose: (() => Promise<void>) | undefined;
    try {
        const configPath = join(temporary, 'wrangler.json');
        await writeFile(configPath, JSON.stringify({
            ...base, main:join(workerRoot, 'src/index.ts'), account_id:env.CLOUDFLARE_ACCOUNT_ID,
            d1_databases:[{binding:'db',database_id:database.uuid,database_name:database.name,remote:true}],
        }), {mode:0o600});
        const {getPlatformProxy} = await import('wrangler');
        const proxy = await getPlatformProxy<{db:StorageDatabase}>({
            configPath, envFiles:[], persist:false, remoteBindings:true,
        });
        dispose = () => proxy.dispose();
        const audit = await auditExistingD1(proxy.env.db);
        const timeTravelBookmarks = await inspectTimeTravelBookmarks(
            api + '/d1/database/' + database.uuid + '/time_travel/bookmark', env.CLOUDFLARE_API_TOKEN,
        );
        const directory = join(workerRoot, 'test-results', 'read-only-d1');
        await mkdir(directory, {recursive:true});
        const report = {
            sourceSha:env.GITHUB_SHA ?? null, observedAt:new Date().toISOString(),
            storage:'existing-demo-worker-D1', d1Database:{uuid:database.uuid,name:database.name}, ...audit,
            timeTravelBookmarks, timeTravelRecoveryVerified:false,
        };
        await writeFile(join(directory,'result.json'), JSON.stringify(report, null, 2) + '\n', {mode:0o600});
        if (audit.status !== 'observed') throw Error('Live data preservation findings require review; no mutation attempted.');
        return report;
    } finally {
        await dispose?.();
        await rm(temporary, {recursive:true,force:true});
    }
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
    try { await runRemoteInventory(); console.log('Read-only D1 inventory written; historical preservation is not proved.'); }
    catch { console.error('Read-only D1 inventory failed. No D1/Worker writes attempted; inspect protected evidence.'); process.exitCode = 1; }
}
