/** A2A v1.0.0 Agent Cards declared by agents for their own public addresses (specification/a2a.proto at tag v1.0.0,
 * proto3 JSON names). Weave stores and serves the card as the agent wrote it. It never verifies the contents, fetches
 * the declared URLs or claims the endpoints exist; the directory marks every card agentCardVerified:false.
 * A card lives only while its address is live and public: leaving that state deletes it in the same transaction.
 */
import { atomic, type Env, type Transaction } from './database.ts';
import { enc, fail, invalidField, uuid } from './wire.ts';

export const MAX_CARD_BYTES = 32768;
const BINDINGS = ['JSONRPC', 'GRPC', 'HTTP+JSON'];
// Every string rejects control characters including tab, CR and LF; only free-text descriptions may contain a line feed.
const controls = /[\x00-\x1f\x7f-\x9f]/u;
const proseControls = /[\x00-\x09\x0b-\x1f\x7f-\x9f]/u;
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);

function only(value: unknown, field: string, allowed: string[], required: string[]): Record<string, unknown> {
    if (!record(value)) invalidField(field, 'must be an object');
    const unknown = Object.keys(value).find(key => !allowed.includes(key));
    if (unknown !== undefined) invalidField(field, (unknown.length <= 64 ? unknown : 'a field') + ' is not an accepted Agent Card field');
    const missing = required.find(key => !Object.hasOwn(value, key));
    if (missing !== undefined) invalidField(field + '.' + missing, 'is required');
    return value;
}
function text(value: unknown, field: string, max: number, min = 1, prose = false): string {
    if (typeof value !== 'string' || value.length < min || value.length > max || (prose ? proseControls : controls).test(value))
        invalidField(field, `must be a string of ${min} to ${max} characters without control characters${prose ? ' other than line feeds' : ''}`);
    return value;
}
function strings(value: unknown, field: string, maxItems: number, maxLength: number): string[] {
    if (!Array.isArray(value) || value.length > maxItems) invalidField(field, `must be an array of at most ${maxItems} strings`);
    return value.map((item, index) => text(item, `${field}[${index}]`, maxLength));
}
function httpsUrl(value: unknown, field: string): string {
    const raw = text(value, field, 2048);
    let url: URL;
    try { url = new URL(raw); } catch { return invalidField(field, 'must be an absolute https URL'); }
    // Only the canonical https:// form: WHATWG parsing also accepts https:host and https:\host, which other parsers read differently.
    if (enc.encode(raw).length > 2048 || /[\x00-\x20\\]/u.test(raw) || !raw.startsWith('https://') || url.protocol !== 'https:' || !url.hostname || url.username || url.password)
        invalidField(field, 'must be an absolute https:// URL without credentials');
    if (enc.encode(url.href).length > 2048) invalidField(field, 'must be at most 2048 bytes once normalized');
    return url.href;
}

const optionalText = (item: Record<string, unknown>, key: string, field: string, max: number) =>
    item[key] === undefined ? {} : {[key]: text(item[key], field + '.' + key, max, 1, key === 'description')};
const optionalUrl = (item: Record<string, unknown>, key: string, field: string) =>
    item[key] === undefined ? {} : {[key]: httpsUrl(item[key], field + '.' + key)};
const optionalBool = (item: Record<string, unknown>, key: string, field: string) => {
    if (item[key] === undefined) return {};
    if (typeof item[key] !== 'boolean') invalidField(field + '.' + key, 'must be true or false');
    return {[key]: item[key]};
};
// a2a.proto v1.0.0 APIKeySecurityScheme.location: "query", "header" or "cookie".
const apiKeyLocation = (value: unknown, field: string): string => {
    if (typeof value !== 'string' || !['query', 'header', 'cookie'].includes(value)) invalidField(field, 'must be query, header or cookie');
    return value;
};
const nameKey = /^[A-Za-z0-9._-]{1,64}$/;

/** Free-form JSON (extension params): depth <= 4, <= 64 members in total, strings without control characters, <= 4 KiB. */
function freeObject(value: unknown, field: string): Record<string, unknown> {
    if (!record(value)) invalidField(field, 'must be an object');
    let members = 0;
    const walk = (node: unknown, depth: number): void => {
        if (typeof node === 'string') { text(node, field, 1024, 0); return; }
        if (node === null || typeof node === 'boolean' || typeof node === 'number' && Number.isFinite(node)) return;
        if (depth > 4) invalidField(field, 'is nested too deeply (at most 4 levels)');
        if (Array.isArray(node)) for (const child of node) { if (++members > 64) invalidField(field, 'has too many members (at most 64 in total)'); walk(child, depth + 1); }
        else if (record(node)) for (const [key, child] of Object.entries(node)) {
            if (++members > 64) invalidField(field, 'has too many members (at most 64 in total)');
            text(key, field, 128); walk(child, depth + 1);
        } else invalidField(field, 'must contain only JSON data');
    };
    walk(value, 1);
    if (enc.encode(JSON.stringify(value)).length > 4096) invalidField(field, 'must serialize to at most 4 KiB');
    return value;
}
function scopes(value: unknown, field: string): Record<string, string> {
    if (!record(value) || Object.keys(value).length > 64) invalidField(field, 'must be an object of at most 64 scopes');
    return Object.fromEntries(Object.entries(value).map(([name, description]) =>
        [text(name, field, 128), text(description, field + '.' + name, 256, 0)]));
}

function checkCapabilities(value: unknown): Record<string, unknown> {
    const field = 'card.capabilities';
    const input = only(value, field, ['streaming', 'pushNotifications', 'extendedAgentCard', 'extensions'], []);
    const result: Record<string, unknown> = {
        ...optionalBool(input, 'streaming', field), ...optionalBool(input, 'pushNotifications', field), ...optionalBool(input, 'extendedAgentCard', field)};
    if (input.extensions !== undefined) {
        if (!Array.isArray(input.extensions) || input.extensions.length > 16) invalidField(field + '.extensions', 'must be an array of at most 16 extensions');
        result.extensions = input.extensions.map((entry, index) => {
            const path = `${field}.extensions[${index}]`;
            const item = only(entry, path, ['uri', 'description', 'required', 'params'], ['uri']);
            return {uri: httpsUrl(item.uri, path + '.uri'), ...optionalText(item, 'description', path, 1024), ...optionalBool(item, 'required', path),
                ...(item.params === undefined ? {} : {params: freeObject(item.params, path + '.params')})};
        });
    }
    return result;
}

// OAuthFlows members (a2a.proto v1.0.0): exactly one flow, with its own URL members and scopes map.
const flowFields: Record<string, {urls: string[]; optionalUrls: string[]; scopesRequired: boolean; bool?: string}> = {
    authorizationCode: {urls: ['authorizationUrl', 'tokenUrl'], optionalUrls: ['refreshUrl'], scopesRequired: true, bool: 'pkceRequired'},
    clientCredentials: {urls: ['tokenUrl'], optionalUrls: ['refreshUrl'], scopesRequired: true},
    implicit: {urls: [], optionalUrls: ['authorizationUrl', 'refreshUrl'], scopesRequired: false},
    password: {urls: [], optionalUrls: ['tokenUrl', 'refreshUrl'], scopesRequired: false},
    deviceCode: {urls: ['deviceAuthorizationUrl', 'tokenUrl'], optionalUrls: ['refreshUrl'], scopesRequired: true}
};
function checkFlows(value: unknown, field: string): Record<string, unknown> {
    const input = only(value, field, Object.keys(flowFields), []);
    const kinds = Object.keys(input);
    if (kinds.length !== 1) invalidField(field, 'must contain exactly one OAuth flow');
    const kind = kinds[0], spec = flowFields[kind], path = field + '.' + kind;
    const urls = [...spec.urls, ...spec.optionalUrls];
    const flow = only(input[kind], path, [...urls, 'scopes', ...(spec.bool ? [spec.bool] : [])], [...spec.urls, ...(spec.scopesRequired ? ['scopes'] : [])]);
    return {[kind]: {...Object.fromEntries(urls.filter(key => flow[key] !== undefined).map(key => [key, httpsUrl(flow[key], path + '.' + key)])),
        ...(flow.scopes === undefined ? {} : {scopes: scopes(flow.scopes, path + '.scopes')}), ...(spec.bool ? optionalBool(flow, spec.bool, path) : {})}};
}

// SecurityScheme oneof members and their fields (a2a.proto v1.0.0, proto3 JSON names).
const schemeKinds: Record<string, {allowed: string[]; required: string[]; build: (item: Record<string, unknown>, field: string) => Record<string, unknown>}> = {
    apiKeySecurityScheme: {allowed: ['description', 'location', 'name'], required: ['location', 'name'], build: (item, field) =>
        ({...optionalText(item, 'description', field, 1024), location: apiKeyLocation(item.location, field + '.location'), name: text(item.name, field + '.name', 128)})},
    httpAuthSecurityScheme: {allowed: ['description', 'scheme', 'bearerFormat'], required: ['scheme'], build: (item, field) =>
        ({...optionalText(item, 'description', field, 1024), scheme: text(item.scheme, field + '.scheme', 64), ...optionalText(item, 'bearerFormat', field, 64)})},
    oauth2SecurityScheme: {allowed: ['description', 'flows', 'oauth2MetadataUrl'], required: ['flows'], build: (item, field) =>
        ({...optionalText(item, 'description', field, 1024), flows: checkFlows(item.flows, field + '.flows'), ...optionalUrl(item, 'oauth2MetadataUrl', field)})},
    openIdConnectSecurityScheme: {allowed: ['description', 'openIdConnectUrl'], required: ['openIdConnectUrl'], build: (item, field) =>
        ({...optionalText(item, 'description', field, 1024), openIdConnectUrl: httpsUrl(item.openIdConnectUrl, field + '.openIdConnectUrl')})},
    mtlsSecurityScheme: {allowed: ['description'], required: [], build: (item, field) => optionalText(item, 'description', field, 1024)}
};
function checkSecuritySchemes(value: unknown): Record<string, unknown> {
    const field = 'card.securitySchemes';
    if (!record(value) || Object.keys(value).length > 16) invalidField(field, 'must be an object of at most 16 schemes');
    return Object.fromEntries(Object.entries(value).map(([name, entry]) => {
        if (!nameKey.test(name)) invalidField(field, 'scheme names must be 1 to 64 characters of letters, digits, ".", "_" or "-"');
        const path = field + '.' + name;
        const wrapper = only(entry, path, Object.keys(schemeKinds), []);
        const kinds = Object.keys(wrapper);
        if (kinds.length !== 1) invalidField(path, 'must contain exactly one scheme kind');
        const spec = schemeKinds[kinds[0]];
        return [name, {[kinds[0]]: spec.build(only(wrapper[kinds[0]], path + '.' + kinds[0], spec.allowed, spec.required), path + '.' + kinds[0])}];
    }));
}
function checkSecurityRequirements(value: unknown, declared: Record<string, unknown>): unknown[] {
    const field = 'card.securityRequirements';
    if (!Array.isArray(value) || value.length > 16) invalidField(field, 'must be an array of at most 16 requirements');
    return value.map((entry, index) => {
        const path = `${field}[${index}]`;
        const requirement = only(entry, path, ['schemes'], []);
        if (requirement.schemes === undefined) return {};
        if (!record(requirement.schemes) || Object.keys(requirement.schemes).length > 16) invalidField(path + '.schemes', 'must be an object of at most 16 schemes');
        return {schemes: Object.fromEntries(Object.entries(requirement.schemes).map(([name, list]) => {
            if (!Object.hasOwn(declared, name)) invalidField(path + '.schemes', 'may only name schemes declared in securitySchemes');
            const item = only(list, `${path}.schemes.${name}`, ['list'], []);
            return [name, {list: item.list === undefined ? [] : strings(item.list, `${path}.schemes.${name}.list`, 64, 128)}];
        }))};
    });
}

/** The validated card rebuilt from accepted fields only, so unknown fields are never stored or served. */
export function checkAgentCard(value: unknown): Record<string, unknown> {
    const input = only(value, 'card', ['name', 'description', 'version', 'supportedInterfaces', 'skills', 'defaultInputModes',
        'defaultOutputModes', 'capabilities', 'securitySchemes', 'securityRequirements'],
        ['name', 'description', 'version', 'supportedInterfaces', 'skills', 'defaultInputModes', 'defaultOutputModes', 'capabilities']);
    if (!Array.isArray(input.supportedInterfaces) || input.supportedInterfaces.length < 1 || input.supportedInterfaces.length > 4)
        invalidField('card.supportedInterfaces', 'must be an array of 1 to 4 interfaces');
    if (!Array.isArray(input.skills) || input.skills.length > 32) invalidField('card.skills', 'must be an array of at most 32 skills');
    const card: Record<string, unknown> = {
        name: text(input.name, 'card.name', 128),
        description: text(input.description, 'card.description', 1024, 1, true),
        version: text(input.version, 'card.version', 32),
        supportedInterfaces: input.supportedInterfaces.map((entry, index) => {
            const field = `card.supportedInterfaces[${index}]`;
            const item = only(entry, field, ['url', 'protocolBinding', 'protocolVersion'], ['url', 'protocolBinding', 'protocolVersion']);
            if (typeof item.protocolBinding !== 'string' || !BINDINGS.includes(item.protocolBinding))
                invalidField(field + '.protocolBinding', 'must be JSONRPC, GRPC or HTTP+JSON');
            if (item.protocolVersion !== '1.0') invalidField(field + '.protocolVersion', 'must be "1.0"');
            return {url: httpsUrl(item.url, field + '.url'), protocolBinding: item.protocolBinding, protocolVersion: '1.0'};
        }),
        skills: input.skills.map((entry, index) => {
            const field = `card.skills[${index}]`;
            const item = only(entry, field, ['id', 'name', 'description', 'tags'], ['id', 'name', 'description', 'tags']);
            return {id: text(item.id, field + '.id', 128), name: text(item.name, field + '.name', 128),
                description: text(item.description, field + '.description', 1024, 1, true), tags: strings(item.tags, field + '.tags', 16, 64)};
        }),
        defaultInputModes: strings(input.defaultInputModes, 'card.defaultInputModes', 16, 128),
        defaultOutputModes: strings(input.defaultOutputModes, 'card.defaultOutputModes', 16, 128),
        capabilities: checkCapabilities(input.capabilities)
    };
    if (input.securitySchemes !== undefined) card.securitySchemes = checkSecuritySchemes(input.securitySchemes);
    if (input.securityRequirements !== undefined)
        card.securityRequirements = checkSecurityRequirements(input.securityRequirements, (card.securitySchemes ?? {}) as Record<string, unknown>);
    if (enc.encode(JSON.stringify(card)).length > MAX_CARD_BYTES) invalidField('card', 'must serialize to at most 32 KiB');
    return card;
}

/** Stores, replaces (card) or removes (null) the card of the caller's own address. Nothing changes on a refusal. */
export async function publishAgentCard(env: Env, owner: string, addressId: string, card: Record<string, unknown> | null,
    guard: (tx: Transaction) => Promise<void> = async () => {}): Promise<unknown> {
    uuid(addressId);
    const serialized = card === null ? null : JSON.stringify(card);
    return atomic(env.db, async tx => {
        await guard(tx);
        const address = await tx.get('card', '', addressId);
        if (!address || !address._address || address.ownerMailboxId !== owner) fail(404, 'unavailable');
        if (serialized === null) {
            tx.writes.push(tx.db.prepare('DELETE FROM address_agent_cards WHERE address_id=? AND account_id=?').bind(addressId, owner));
            return {addressId, agentCard: null};
        }
        if (address._deleted) fail(404, 'unavailable');
        if (!address._address.isPublic) invalidField('addressId', 'must be a public address; create it with ensure_my_address isPublic:true first');
        tx.writes.push(tx.db.prepare(`INSERT INTO address_agent_cards(address_id,account_id,card_json,updated_at) VALUES(?,?,?,CAST(? AS INTEGER))
            ON CONFLICT(address_id) DO UPDATE SET card_json=excluded.card_json,updated_at=excluded.updated_at`)
            .bind(addressId, owner, serialized, tx.store.now.toString()));
        return {addressId, agentCard: card};
    });
}

/** Stages the deletion of an address's card; used when the address is revoked or stops being public. */
export function removeAgentCard(tx: Transaction, addressId: string): void {
    tx.writes.push(tx.db.prepare('DELETE FROM address_agent_cards WHERE address_id=?').bind(addressId));
}

/** Parsed cards for addresses already selected as live and public. */
export async function agentCards(tx: Transaction, addressIds: string[]): Promise<Map<string, unknown>> {
    if (!addressIds.length) return new Map();
    const rows = await tx.db.prepare(`SELECT address_id,card_json FROM address_agent_cards WHERE address_id IN (${addressIds.map(() => '?').join(',')})`)
        .bind(...addressIds).all<{address_id: string; card_json: string}>();
    return new Map(rows.results.map(row => [row.address_id, JSON.parse(row.card_json)]));
}

/** Directory views gain agentCard (null when none); a present card is always marked as unverified agent-declared content. */
export async function withAgentCards<T extends {addressId: string}>(tx: Transaction, views: T[]): Promise<(T & {agentCard: unknown; agentCardVerified?: false})[]> {
    const found = await agentCards(tx, views.map(view => view.addressId));
    return views.map(view => found.has(view.addressId) ? {...view, agentCard: found.get(view.addressId), agentCardVerified: false as const} : {...view, agentCard: null});
}
