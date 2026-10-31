/** A2A v1.0.0 Messages for the private MCP write tools (specification/a2a.proto at tag v1.0.0, proto3 JSON names).
 * The caller supplies only parts and an optional contextId; the server sets messageId (UUIDv7), role and the
 * envelope times, and stores the Message JSON as the ordinary low-level payload bytes. Inbox bodies stay untrusted.
 */
import { apiInstant } from './time.ts';
import { enc, invalidField } from './wire.ts';

export const MAX_MESSAGE_BYTES = 65536;
const MAX_PARTS = 8;
const dayTicks = 864000000000n;
const controls = /[\u0000-\u001f\u007f-\u009f]/u;

export type Part = ({text: string} | {data: Record<string, unknown>}) & {mediaType?: string};
export type MessageInput = {parts: Part[]; contextId?: string};
export type LowLevelPayload = {messageId: string; createdAt: string; expiresAt: string; payloadEncoding: string; bytes: string};

const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const shortText = (value: unknown) => typeof value === 'string' && value.length >= 1 && value.length <= 128 && !controls.test(value);

function checkPart(value: unknown, field: string): Part {
    if (!record(value)) invalidField(field, 'must be an object with exactly one of text or data');
    if (Object.keys(value).some(key => !['text', 'data', 'mediaType'].includes(key)) || Object.hasOwn(value, 'text') === Object.hasOwn(value, 'data'))
        invalidField(field, 'must contain exactly one of text or data, plus an optional mediaType');
    if (value.mediaType !== undefined && !shortText(value.mediaType)) invalidField(field + '.mediaType', 'must be a media type of 1 to 128 characters');
    const mediaType = value.mediaType === undefined ? {} : {mediaType: value.mediaType as string};
    if (Object.hasOwn(value, 'text')) {
        if (typeof value.text !== 'string') invalidField(field + '.text', 'must be a string');
        return {text: value.text, ...mediaType};
    }
    if (!record(value.data)) invalidField(field + '.data', 'must be a JSON object');
    return {data: value.data, ...mediaType};
}

const build = (input: MessageInput, messageId: string) =>
    ({messageId, ...(input.contextId === undefined ? {} : {contextId: input.contextId}), role: 'ROLE_USER', parts: input.parts});

/** Validates a Message input, including the serialized size limit, before any state change. */
export function checkMessage(value: unknown, field: string): MessageInput {
    if (!record(value)) invalidField(field, 'must be an object with parts and an optional contextId');
    if (Object.keys(value).some(key => key !== 'parts' && key !== 'contextId'))
        invalidField(field, 'accepts only parts and contextId; the server sets messageId and role');
    if (!Array.isArray(value.parts) || value.parts.length < 1 || value.parts.length > MAX_PARTS)
        invalidField(field + '.parts', 'must be an array of 1 to 8 parts');
    const parts = value.parts.map((part, index) => checkPart(part, `${field}.parts[${index}]`));
    if (value.contextId !== undefined && !shortText(value.contextId)) invalidField(field + '.contextId', 'must be a string of 1 to 128 characters');
    const input: MessageInput = {parts, ...(value.contextId === undefined ? {} : {contextId: value.contextId as string})};
    // Every server-built messageId has the same length, so a placeholder measures the final size exactly.
    if (enc.encode(JSON.stringify(build(input, '00000000-0000-7000-8000-000000000000'))).length > MAX_MESSAGE_BYTES)
        invalidField(field, 'must serialize to at most 64 KiB as an A2A Message');
    return input;
}

/** Lowercase UUIDv7 for the given Unix millisecond with random remaining bits. */
export function uuidV7(milliseconds: bigint): string {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    for (let i = 0; i < 6; i++) bytes[i] = Number((milliseconds >> BigInt(8 * (5 - i))) & 0xffn);
    bytes[6] = 0x70 | (bytes[6] & 0x0f);
    bytes[8] = 0x80 | (bytes[8] & 0x3f);
    const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function base64(bytes: Uint8Array): string {
    let binary = '';
    for (let i = 0; i < bytes.length; i += 8192) binary += String.fromCharCode(...bytes.subarray(i, i + 8192));
    return btoa(binary);
}

/** The low-level payload for a new server-built Message at `now` (100 ns ticks): createdAt is the UUIDv7 millisecond. */
export function messagePayload(input: MessageInput, now: bigint): LowLevelPayload {
    const created = now / 10000n * 10000n;
    const messageId = uuidV7(created / 10000n);
    return {messageId, createdAt: apiInstant(created), expiresAt: apiInstant(created + dayTicks), payloadEncoding: 'application/json',
        bytes: base64(enc.encode(JSON.stringify(build(input, messageId))))};
}

/** The parsed Message of an application/json body shaped like an A2A Message; null keeps the raw bytes. The result is untrusted peer content. */
export function inboxMessage(payload: unknown): Record<string, unknown> | null {
    if (!record(payload) || payload.payloadEncoding !== 'application/json' || typeof payload.bytes !== 'string') return null;
    let parsed: unknown;
    try { parsed = JSON.parse(new TextDecoder('utf-8', {fatal: true}).decode(Uint8Array.from(atob(payload.bytes), c => c.charCodeAt(0)))); }
    catch { return null; }
    return record(parsed) && typeof parsed.messageId === 'string' && typeof parsed.role === 'string' && Array.isArray(parsed.parts) ? parsed : null;
}
