/** Field-level pre-checks for private MCP tool arguments. They mirror the
 * authoritative protocol validation so a model can correct its own call; the
 * protocol validation still runs afterwards and remains the decision.
 * Reasons describe the rule only and never echo the submitted value.
 */
import { invalidField } from './wire';
import { ticks } from './protocol';

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const uuidV7Pattern = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const payloadKeys = ['messageId', 'createdAt', 'expiresAt', 'payloadEncoding', 'bytes'];
const maxLifetimeTicks = 864000000000n; // ten days
const maxFutureTicks = 300000000n; // thirty seconds

const record = (value: unknown): value is Record<string, unknown> =>
    !!value && typeof value === 'object' && !Array.isArray(value);

/** Milliseconds encoded in the first 48 bits of a UUIDv7. */
export const uuidV7Millis = (id: string): number => Number(BigInt('0x' + id.replaceAll('-', '').slice(0, 12)));

export function checkUuid(value: unknown, field: string): asserts value is string {
    if (typeof value !== 'string' || !uuidPattern.test(value)) invalidField(field, 'must be a lowercase UUID');
}

function timestamp(value: unknown, field: string): bigint {
    try { return ticks(value); }
    catch { return invalidField(field, 'must be an ISO 8601 timestamp such as 2026-01-01T00:00:00.000Z'); }
}

export function checkPayload(value: unknown, field: string): asserts value is Record<string, unknown> {
    if (!record(value)) invalidField(field, 'must be an object');
    const keys = Object.keys(value);
    if (payloadKeys.some(key => !keys.includes(key)) || keys.some(key => !payloadKeys.includes(key)))
        invalidField(field, 'must contain exactly ' + payloadKeys.join(', '));
    const messageId = value.messageId;
    if (typeof messageId !== 'string' || !uuidV7Pattern.test(messageId))
        invalidField(field + '.messageId', 'must be a lowercase UUIDv7 generated from the same millisecond as createdAt');
    const created = timestamp(value.createdAt, field + '.createdAt');
    const encoded = uuidV7Millis(messageId);
    if (BigInt(encoded) !== created / 10000n)
        invalidField(field + '.createdAt', 'must equal the millisecond timestamp encoded in messageId (' + new Date(encoded).toISOString() + ')');
    if (created - BigInt(Date.now()) * 10000n > maxFutureTicks)
        invalidField(field + '.createdAt', 'must not be more than 30 seconds in the future');
    const expires = timestamp(value.expiresAt, field + '.expiresAt');
    if (expires <= created || expires - created > maxLifetimeTicks)
        invalidField(field + '.expiresAt', 'must be after createdAt and at most 10 days later');
    const encoding = value.payloadEncoding;
    if (typeof encoding !== 'string' || !encoding.trim() || encoding.length > 128 || /[\u0000-\u001f\u007f-\u009f]/u.test(encoding))
        invalidField(field + '.payloadEncoding', 'must be a non-empty media type such as text/plain');
    const bytes = value.bytes;
    if (typeof bytes !== 'string' || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(bytes.replace(/[\t\r\n ]/g, '')))
        invalidField(field + '.bytes', 'must be standard padded base64');
}

/** A contact requestId is the request payload's UUIDv7; default it when omitted. */
export function contactRequestId(requestId: unknown, payload: Record<string, unknown>): string {
    if (requestId === undefined) return payload.messageId as string;
    if (typeof requestId !== 'string' || !uuidV7Pattern.test(requestId) ||
        uuidV7Millis(requestId) !== uuidV7Millis(payload.messageId as string))
        invalidField('requestId', 'must be a UUIDv7 with the same timestamp as payload.createdAt; omit it or use payload.messageId');
    return requestId;
}
