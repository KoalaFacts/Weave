import assert from 'node:assert/strict';
import {test} from 'node:test';
import {checkMessage, inboxMessage, messagePayload, MAX_MESSAGE_BYTES} from '../src/a2a-message.ts';
import {Problem} from '../src/wire.ts';

const invalid = (action: () => unknown, field: string) =>
    assert.throws(action, (error: unknown) => error instanceof Problem && error.status === 400 && error.detail?.field === field);
const decode = (bytes: string) => JSON.parse(Buffer.from(bytes, 'base64').toString('utf8'));
const ticks = (ms: number) => BigInt(ms) * 10000n;

test('messagePayload_TextPart_BuildsA2aMessageWithUuidV7FromClock', () => {
    const at = Date.UTC(2026, 10, 1, 12, 0, 0, 123);
    const payload = messagePayload(checkMessage({parts: [{text: 'hello'}], contextId: 'thread-1'}, 'message'), ticks(at) + 4567n);
    assert.match(payload.messageId, /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    assert.equal(Number(BigInt('0x' + payload.messageId.replaceAll('-', '').slice(0, 12))), at);
    assert.equal(payload.createdAt, '2026-11-01T12:00:00.123Z');
    assert.equal(payload.expiresAt, '2026-11-02T12:00:00.123Z');
    assert.equal(payload.payloadEncoding, 'application/json');
    assert.deepEqual(decode(payload.bytes), {messageId: payload.messageId, contextId: 'thread-1', role: 'ROLE_USER', parts: [{text: 'hello'}]});
});

test('messagePayload_NonAsciiText_EncodesUtf8', () => {
    const payload = messagePayload(checkMessage({parts: [{text: '你好 \u{1F600}', mediaType: 'text/plain'}]}, 'message'), ticks(Date.UTC(2026, 0, 1)));
    assert.deepEqual(decode(payload.bytes).parts, [{text: '你好 \u{1F600}', mediaType: 'text/plain'}]);
});

test('checkMessage_InvalidShapes_NameTheField', () => {
    invalid(() => checkMessage('text', 'message'), 'message');
    invalid(() => checkMessage({parts: [{text: 'x'}], messageId: 'x'}, 'message'), 'message');
    invalid(() => checkMessage({parts: []}, 'message'), 'message.parts');
    invalid(() => checkMessage({parts: Array.from({length: 9}, () => ({text: 'x'}))}, 'message'), 'message.parts');
    invalid(() => checkMessage({parts: [{text: 'x', data: {a: 1}}]}, 'message'), 'message.parts[0]');
    invalid(() => checkMessage({parts: [{}]}, 'message'), 'message.parts[0]');
    invalid(() => checkMessage({parts: [{text: 1}]}, 'message'), 'message.parts[0].text');
    invalid(() => checkMessage({parts: [{data: [1]}]}, 'message'), 'message.parts[0].data');
    invalid(() => checkMessage({parts: [{text: 'x', url: 'https://x'}]}, 'message'), 'message.parts[0]');
    invalid(() => checkMessage({parts: [{text: 'x', mediaType: 'm'.repeat(129)}]}, 'message'), 'message.parts[0].mediaType');
    invalid(() => checkMessage({parts: [{text: 'x'}], contextId: 'c'.repeat(129)}, 'message'), 'message.contextId');
    invalid(() => checkMessage({parts: [{text: 'x'}], contextId: ''}, 'message'), 'message.contextId');
});

test('checkMessage_SerializedSizeLimit_CountsWholeMessage', () => {
    // The fixed envelope is the server-built messageId and role; find the largest text that still fits.
    const overhead = JSON.stringify({messageId: '00000000-0000-7000-8000-000000000000', role: 'ROLE_USER', parts: [{text: ''}]}).length;
    checkMessage({parts: [{text: 'a'.repeat(MAX_MESSAGE_BYTES - overhead)}]}, 'message');
    invalid(() => checkMessage({parts: [{text: 'a'.repeat(MAX_MESSAGE_BYTES - overhead + 1)}]}, 'message'), 'message');
    // Escaped characters count at their serialized size.
    invalid(() => checkMessage({parts: [{text: '"'.repeat(MAX_MESSAGE_BYTES / 2)}]}, 'message'), 'message');
});

test('inboxMessage_OnlyWellFormedA2aJson_IsParsed', () => {
    const body = (value: unknown, payloadEncoding = 'application/json') =>
        ({payloadEncoding, bytes: Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)).toString('base64')});
    const message = {messageId: 'peer-chosen', role: 'ROLE_AGENT', parts: [{text: 'hi'}], metadata: {kind: 'key-added'}};
    assert.deepEqual(inboxMessage(body(message)), message);
    assert.equal(inboxMessage(body(message, 'text/plain')), null);
    assert.equal(inboxMessage(body('{"messageId":')), null);
    assert.equal(inboxMessage(body([message])), null);
    assert.equal(inboxMessage(body({...message, messageId: 1})), null);
    assert.equal(inboxMessage(body({...message, parts: 'x'})), null);
    assert.equal(inboxMessage({payloadEncoding: 'application/json', bytes: Buffer.from([0xff, 0xfe]).toString('base64')}), null);
    assert.equal(inboxMessage(null), null);
});
