/**
 * Credentials an owner connects for one of their agents. Connecting verifies the provider account, seals the secret
 * bound to its row and writes a fresh revision, so every approval made under an earlier revision stops being executable.
 */
import { atomic, type Env, type Transaction } from './database.ts';
import { writesRefused } from './humans.ts';
import { reauthFutureSkewMs, reauthWindowMs, type OwnerDecision } from './owner-actions.ts';
import { sealSecret } from './credential-vault.ts';
import { connectYoutube, youtubeConfigured, type ConnectRefusal } from './youtube-provider.ts';
import { provider, publishingAdapter, type MediaRules, type SettingRules, type TextRules } from './external-providers.ts';
import { apiTime, nowUtc } from './time.ts';
import { hasVisibleCharacter, visibleExcerpt } from './visible-text.ts';
import type { ActionCaller } from './external-actions.ts';

export type ConnectProvider = 'youtube';
export type ConnectCredentialInput = {humanId: string; authAt: number; agentId: string; provider: ConnectProvider; code: string; verifier: string; redirectUri: string};
export type ConnectCredentialResult = {ok: true; credentialRef: string; targetDisplay: string}
    | {ok: false; reason: 'stale-authentication' | 'frozen' | 'not-found' | 'provider-unavailable' | ConnectRefusal | 'unavailable'};
export type OwnerCredential = {credentialRef: string; provider: string; targetAccount: string; targetDisplay: string; revision: string; connectedAt: string};

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const codePattern = /^[\x21-\x7e]{1,2048}$/, verifierPattern = /^[A-Za-z0-9._~-]{43,128}$/;
/** One credential per provider per agent: the reference is the provider name. */
const referenceFor = (provider: ConnectProvider) => provider;

/** The human owns the agent and is not frozen; anything else reads as not found (or frozen). */
async function ownership(tx: Transaction, humanId: string, agentId: string): Promise<'ok' | 'frozen' | 'not-found'> {
    const row = await tx.db.prepare('SELECT CAST(h.frozen_at AS TEXT) frozen_at FROM accounts a JOIN humans h ON h.human_id=a.human_id WHERE a.account_id=? AND a.human_id=?')
        .bind(agentId, humanId).first<{frozen_at: string | null}>();
    return !row ? 'not-found' : row.frozen_at !== null ? 'frozen' : 'ok';
}

function validInput(input: unknown): input is ConnectCredentialInput {
    const value = input as Record<string, unknown> | null;
    return typeof value === 'object' && value !== null && Object.keys(value).length === 7
        && typeof value.humanId === 'string' && uuidPattern.test(value.humanId) && typeof value.agentId === 'string' && uuidPattern.test(value.agentId)
        && typeof value.authAt === 'number' && Number.isSafeInteger(value.authAt) && value.provider === 'youtube'
        && typeof value.code === 'string' && codePattern.test(value.code) && typeof value.verifier === 'string' && verifierPattern.test(value.verifier)
        && typeof value.redirectUri === 'string' && URL.canParse(value.redirectUri) && new URL(value.redirectUri).protocol === 'https:';
}

/**
 * Owner RPC after the provider's OAuth callback. Ownership is checked before the code is exchanged and again in the
 * write; storage and provider failures are categories, never thrown to the authorization server.
 */
export async function connectExternalCredential(env: Env, input: ConnectCredentialInput, clock: () => bigint = nowUtc): Promise<ConnectCredentialResult> {
    if (writesRefused(env)) return {ok: false, reason: 'unavailable'};
    if (!validInput(input)) return {ok: false, reason: 'not-found'};
    const nowMs = Number(clock() / 10000n);
    if (nowMs - input.authAt > reauthWindowMs || input.authAt > nowMs + reauthFutureSkewMs) return {ok: false, reason: 'stale-authentication'};
    if (!youtubeConfigured(env)) return {ok: false, reason: 'provider-unavailable'};
    try {
        const before = await atomic(env.db, tx => ownership(tx, input.humanId, input.agentId), clock);
        if (before !== 'ok') return {ok: false, reason: before};
        const verified = await connectYoutube(env, input);
        if (typeof verified === 'string') return {ok: false, reason: verified};
        const connected = {...verified, targetDisplay: shownName(verified)};
        const credentialRef = referenceFor(input.provider), revision = crypto.randomUUID();
        const sealed = await sealSecret(env.EXTERNAL_CREDENTIAL_KEY, {accountId: input.agentId, credentialRef, provider: input.provider, revision}, connected.secret);
        return await atomic(env.db, async tx => {
            const now = await ownership(tx, input.humanId, input.agentId);
            if (now !== 'ok') return {ok: false, reason: now} as const;
            const stamp = tx.store.now.toString();
            tx.writes.push(tx.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,secret,created_at,updated_at)
                VALUES(?,?,?,?,?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))
                ON CONFLICT(account_id,credential_ref) DO UPDATE SET provider=excluded.provider,revision=excluded.revision,target_account=excluded.target_account,
                target_display=excluded.target_display,secret=excluded.secret,revoked_at=NULL,updated_at=excluded.updated_at`)
                .bind(input.agentId, credentialRef, input.provider, revision, connected.targetAccount, connected.targetDisplay, sealed.buffer as ArrayBuffer, stamp, stamp));
            return {ok: true, credentialRef, targetDisplay: connected.targetDisplay} as const;
        }, clock);
    } catch {
        console.error('external-credential: connect failed');
        return {ok: false, reason: 'unavailable'};
    }
}

/** Live credentials of the owner's agents, for the owner page. */
export async function ownedCredentials(tx: Transaction, agentIds: string[]): Promise<(OwnerCredential & {agentId: string})[]> {
    if (agentIds.length === 0) return [];
    const rows = (await tx.db.prepare(`SELECT account_id,credential_ref,provider,target_account,target_display,revision,CAST(updated_at AS TEXT) updated_at
        FROM external_credentials WHERE revoked_at IS NULL AND account_id IN (${agentIds.map(() => '?').join(',')}) ORDER BY account_id,credential_ref`)
        .bind(...agentIds).all<{account_id: string; credential_ref: string; provider: string; target_account: string; target_display: string; revision: string; updated_at: string}>()).results;
    return rows.map(row => ({agentId: row.account_id, credentialRef: row.credential_ref, provider: row.provider, targetAccount: row.target_account,
        targetDisplay: row.target_display, revision: row.revision, connectedAt: apiTime(row.updated_at)!}));
}

/** A live credential as its agent sees it: what to pass as credentialRef, which operations work now and the content they take. */
export type AgentCredential = {credentialRef: string; provider: string; targetAccount: string; targetDisplay: string; operations: string[]; content: string | null;
    textRules: TextRules | null; mediaRules: MediaRules | null; settingRules: SettingRules | null};

/** GET /v1/external-credentials: the agent's live credentials. A provider this deployment cannot use lists no operations. */
export async function listAgentCredentials(env: Env, caller: ActionCaller): Promise<{items: AgentCredential[]}> {
    return atomic(env.db, async tx => {
        await caller.check(tx);
        const rows = (await tx.db.prepare(`SELECT credential_ref,provider,target_account,target_display FROM external_credentials
            WHERE account_id=? AND revoked_at IS NULL ORDER BY credential_ref`).bind(caller.accountId)
            .all<{credential_ref: string; provider: string; target_account: string; target_display: string}>()).results;
        return {items: rows.map(row => {
            const usable = provider(env, row.provider);
            return {credentialRef: row.credential_ref, provider: row.provider, targetAccount: row.target_account, targetDisplay: row.target_display,
                operations: usable ? [...usable.operations] : [], content: usable ? usable.describeContent(row.target_account) : null,
                textRules: usable?.textRules?.(row.target_account) ?? null, mediaRules: usable?.mediaRules ?? null,
                settingRules: usable?.settingRules?.(row.target_account) ?? null};
        })};
    });
}

/** Owner disconnect: erases the sealed secret. A protective action, so no fresh authentication. */
export async function revokeExternalCredential(tx: Transaction, humanId: string, agentId: string, credentialRef: string, at: bigint): Promise<OwnerDecision> {
    const owned = await ownership(tx, humanId, agentId);
    if (owned !== 'ok') return {ok: false, reason: owned};
    const live = await tx.db.prepare('SELECT 1 n FROM external_credentials WHERE account_id=? AND credential_ref=? AND revoked_at IS NULL')
        .bind(agentId, credentialRef).first<number>('n');
    if (live === null) return {ok: false, reason: 'not-found'};
    const stamp = at.toString();
    tx.writes.push(tx.db.prepare(`UPDATE external_credentials SET revoked_at=CAST(? AS INTEGER),secret=NULL,updated_at=CAST(? AS INTEGER)
        WHERE account_id=? AND credential_ref=? AND revoked_at IS NULL`).bind(stamp, stamp, agentId, credentialRef));
    return {ok: true};
}

export type ConnectAccountInput = {humanId: string; authAt: number; agentId: string; provider: string; fields: Record<string, string>};
export type ConnectAccountResult = {ok: true; accounts: string[]}
    | {ok: false; reason: 'stale-authentication' | 'frozen' | 'not-found' | 'provider-unavailable' | 'provider-refused' | 'no-channel' | 'unavailable'};
const fieldValue = /^[\x20-\x7e]{1,512}$/;

/**
 * Owner RPC for any publishing adapter: the owner's values for exactly the adapter's connect fields. The platform verifies
 * them and names the accounts they act as; each becomes the credential `<provider>:<ref>` with a fresh revision and the
 * secret fields sealed to its row. For an adapter whose one secret covers all of the agent's accounts, accounts it no
 * longer lists are revoked.
 */
export async function connectAccount(env: Env, input: ConnectAccountInput, clock: () => bigint = nowUtc): Promise<ConnectAccountResult> {
    if (writesRefused(env)) return {ok: false, reason: 'unavailable'};
    const value = input as Record<string, unknown> | null;
    if (typeof value !== 'object' || value === null || Object.keys(value).length !== 5 || typeof value.humanId !== 'string' || !uuidPattern.test(value.humanId)
        || typeof value.agentId !== 'string' || !uuidPattern.test(value.agentId) || typeof value.authAt !== 'number' || !Number.isSafeInteger(value.authAt)
        || typeof value.provider !== 'string' || !value.fields || typeof value.fields !== 'object' || Array.isArray(value.fields)) return {ok: false, reason: 'not-found'};
    const adapter = publishingAdapter(env, input.provider);
    const fields = adapter?.connector.fields ?? [];
    // Exactly the adapter's fields, each printable text once the spaces a paste brings along are trimmed; anything else reads as an unknown form.
    if (adapter && (Object.keys(input.fields).length !== fields.length || fields.some(field => typeof input.fields[field.name] !== 'string')))
        return {ok: false, reason: 'not-found'};
    const values = Object.fromEntries(fields.map(field => [field.name, (input.fields[field.name] as string).trim()]));
    if (fields.some(field => !fieldValue.test(values[field.name]))) return {ok: false, reason: 'not-found'};
    const nowMs = Number(clock() / 10000n);
    if (nowMs - input.authAt > reauthWindowMs || input.authAt > nowMs + reauthFutureSkewMs) return {ok: false, reason: 'stale-authentication'};
    if (!adapter) return {ok: false, reason: 'provider-unavailable'};
    const secretFields = fields.filter(field => field.secret), provider = adapter.name;
    try {
        const before = await atomic(env.db, tx => ownership(tx, input.humanId, input.agentId), clock);
        if (before !== 'ok') return {ok: false, reason: before};
        const accounts = await adapter.connector.verify(values);
        if (accounts === null) return {ok: false, reason: 'provider-refused'};
        if (accounts.length === 0) return {ok: false, reason: 'no-channel'};
        // One secret field is sealed as its value; several are sealed together as one JSON object of their values.
        const secret = secretFields.length === 1 ? values[secretFields[0].name]
            : JSON.stringify(Object.fromEntries(secretFields.map(field => [field.name, values[field.name]])));
        const rows = await Promise.all(accounts.map(async account => {
            const credentialRef = provider + ':' + account.ref, revision = crypto.randomUUID();
            const sealed = await sealSecret(env.EXTERNAL_CREDENTIAL_KEY, {accountId: input.agentId, credentialRef, provider, revision}, secret);
            return {...account, targetDisplay: shownName(account), credentialRef, revision, sealed};
        }));
        return await atomic(env.db, async tx => {
            const now = await ownership(tx, input.humanId, input.agentId);
            if (now !== 'ok') return {ok: false, reason: now} as const;
            const stamp = tx.store.now.toString();
            if (adapter.connector.replaces) {
                const refs = rows.map(row => row.credentialRef);
                tx.writes.push(tx.db.prepare(`UPDATE external_credentials SET revoked_at=CAST(? AS INTEGER),secret=NULL,updated_at=CAST(? AS INTEGER)
                    WHERE account_id=? AND provider=? AND revoked_at IS NULL AND credential_ref NOT IN (${refs.map(() => '?').join(',')})`).bind(stamp, stamp, input.agentId, provider, ...refs));
            }
            for (const row of rows) tx.writes.push(tx.db.prepare(`INSERT INTO external_credentials(account_id,credential_ref,provider,revision,target_account,target_display,secret,created_at,updated_at)
                VALUES(?,?,?,?,?,?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))
                ON CONFLICT(account_id,credential_ref) DO UPDATE SET provider=excluded.provider,revision=excluded.revision,target_account=excluded.target_account,
                target_display=excluded.target_display,secret=excluded.secret,revoked_at=NULL,updated_at=excluded.updated_at`)
                .bind(input.agentId, row.credentialRef, provider, row.revision, row.targetAccount, row.targetDisplay, row.sealed.buffer as ArrayBuffer, stamp, stamp));
            return {ok: true, accounts: rows.map(row => row.targetDisplay)} as const;
        }, clock);
    } catch {
        console.error('external-credential: connect failed');
        return {ok: false, reason: 'unavailable'};
    }
}

/**
 * The account name a provider sent back, as stored and shown on owner and approval pages: invisible, control and bidi
 * characters removed, whitespace collapsed, well formed and at most 400 UTF-16 units (the published bound). A name with
 * no visible character left (only whitespace or default-ignorable characters) reads as the provider-verified account
 * id instead.
 */
function shownName(account: {targetAccount: string; targetDisplay: string}): string {
    const name = visibleExcerpt(account.targetDisplay, 400);
    return hasVisibleCharacter(name) ? name : visibleExcerpt(account.targetAccount, 400);
}
