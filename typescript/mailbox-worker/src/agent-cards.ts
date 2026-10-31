/** A2A v1.0.0 Agent Cards declared by agents for their own public addresses (specification/a2a.proto at tag v1.0.0,
 * proto3 JSON names). Weave stores and serves the card as the agent wrote it. It never verifies the contents, fetches
 * the declared URLs or claims the endpoints exist; the directory marks every card agentCardVerified:false.
 * A card lives only while its address is live and public: leaving that state deletes it in the same transaction.
 */
import { atomic, type Env, type Transaction } from './database.ts';
import { enc, fail, invalidField, uuid } from './wire.ts';

export const MAX_CARD_BYTES = 32768;
const BINDINGS = ['JSONRPC', 'GRPC', 'HTTP+JSON'];
const controls = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/u;
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);

function only(value: unknown, field: string, allowed: string[], required: string[]): Record<string, unknown> {
    if (!record(value)) invalidField(field, 'must be an object');
    const unknown = Object.keys(value).find(key => !allowed.includes(key));
    if (unknown !== undefined) invalidField(field, (unknown.length <= 64 ? unknown : 'a field') + ' is not an accepted Agent Card field');
    const missing = required.find(key => !Object.hasOwn(value, key));
    if (missing !== undefined) invalidField(field + '.' + missing, 'is required');
    return value;
}
function text(value: unknown, field: string, max: number): string {
    if (typeof value !== 'string' || value.length < 1 || value.length > max || controls.test(value))
        invalidField(field, `must be a string of 1 to ${max} characters without control characters`);
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
    if (enc.encode(raw).length > 2048 || /[\u0000- ]/u.test(raw) || url.protocol !== 'https:' || url.username || url.password)
        invalidField(field, 'must be an absolute https URL without credentials');
    return raw;
}

/** The validated card rebuilt from accepted fields only, so unknown fields are never stored or served. */
export function checkAgentCard(value: unknown): Record<string, unknown> {
    const input = only(value, 'card', ['name', 'description', 'version', 'supportedInterfaces', 'skills', 'defaultInputModes',
        'defaultOutputModes', 'capabilities', 'securitySchemes', 'securityRequirements'],
        ['name', 'description', 'version', 'supportedInterfaces', 'skills', 'defaultInputModes', 'defaultOutputModes', 'capabilities']);
    if (!Array.isArray(input.supportedInterfaces) || input.supportedInterfaces.length < 1 || input.supportedInterfaces.length > 4)
        invalidField('card.supportedInterfaces', 'must be an array of 1 to 4 interfaces');
    if (!Array.isArray(input.skills) || input.skills.length > 32) invalidField('card.skills', 'must be an array of at most 32 skills');
    if (!record(input.capabilities)) invalidField('card.capabilities', 'must be an object (use {} to declare no capabilities)');
    const card: Record<string, unknown> = {
        name: text(input.name, 'card.name', 128),
        description: text(input.description, 'card.description', 1024),
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
                description: text(item.description, field + '.description', 1024), tags: strings(item.tags, field + '.tags', 16, 64)};
        }),
        defaultInputModes: strings(input.defaultInputModes, 'card.defaultInputModes', 16, 128),
        defaultOutputModes: strings(input.defaultOutputModes, 'card.defaultOutputModes', 16, 128),
        capabilities: input.capabilities
    };
    if (input.securitySchemes !== undefined) {
        if (!record(input.securitySchemes)) invalidField('card.securitySchemes', 'must be an object');
        card.securitySchemes = input.securitySchemes;
    }
    if (input.securityRequirements !== undefined) {
        if (!Array.isArray(input.securityRequirements)) invalidField('card.securityRequirements', 'must be an array');
        card.securityRequirements = input.securityRequirements;
    }
    if (enc.encode(JSON.stringify(card)).length > MAX_CARD_BYTES) invalidField('card', 'must serialize to at most 32 KiB');
    return card;
}

/** Stores, replaces (card) or removes (null) the card of the caller's own address. Nothing changes on a refusal. */
export async function publishAgentCard(env: Env, owner: string, addressId: string, card: Record<string, unknown> | null): Promise<unknown> {
    uuid(addressId);
    const serialized = card === null ? null : JSON.stringify(card);
    return atomic(env.db, async tx => {
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
