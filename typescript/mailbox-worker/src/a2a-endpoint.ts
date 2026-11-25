/** A2A v1.0 inbound endpoint for a public address (roadmap #586, R1-1): the JSON-RPC 2.0 binding of `SendMessage`.
 * An authenticated Weave agent that is an accepted contact of the address's owner sends an A2A Message; Weave stores it
 * in the owner's inbox exactly like `send_message` and answers with a Message that says only "queued". Weave runs no task,
 * so the answer is never a Task and never says that the recipient read or processed anything. The content stays
 * untrusted peer data. Field names follow specification/a2a.proto (proto3 JSON) as of A2A v1.0.
 */
import { atomic, type Env } from './database.ts';
import { publicAddressOwner } from './addresses.ts';
import { checkMessage, messagePayload, shallowJson, uuidV7 } from './a2a-message.ts';
import { idempotencyClaim } from './mcp-idempotency.ts';
import { sweep } from './cleanup.ts';
import { terminalize, sendWithResolvedConnection } from './messages.ts';
import { connectedGeneration, keyed, withoutSecrets } from './remote-private.ts';
import { boundedBody, fail, Problem, stringify } from './wire.ts';

const MAX_REQUEST_BYTES = 131072, MAX_DEPTH = 32;
/** The only A2A version served; a request without `A2A-Version` is treated as 1.0. */
export const A2A_VERSION = '1.0';
/** Message fields this endpoint accepts. Weave keeps no tasks, so `taskId` and `referenceTaskIds` are refused. */
const messageFields = ['messageId', 'role', 'parts', 'contextId', 'metadata', 'extensions'];

type Id = string | number | null;
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);

class RpcError extends Error {
    constructor(readonly rpcCode: number, readonly reason: string, readonly detail: Record<string, unknown> = {}) { super(reason); }
}
const invalidParams = (field: string, reason: string) => new RpcError(-32602, 'Invalid params', {field, reason});

function answer(id: Id, body: Record<string, unknown>, headers: Record<string, string> = {}): Response {
    return new Response(stringify({jsonrpc: '2.0', id, ...body}), {headers: {'Content-Type': 'application/json', 'Cache-Control': 'no-store',
        'Pragma': 'no-cache', 'A2A-Version': A2A_VERSION, ...headers}});
}
const failure = (id: Id, code: number, message: string, data: Record<string, unknown>, headers?: Record<string, string>) =>
    answer(id, {error: {code, message, data}}, headers);

/** The A2A Message input reduced to what `checkMessage` accepts: the caller's parts and contextId. */
function messageInput(value: unknown): {input: {parts: unknown; contextId?: unknown}; clientMessageId: string} {
    if (!record(value)) throw invalidParams('params.message', 'must be an A2A Message object');
    const unknown = Object.keys(value).find(key => !messageFields.includes(key));
    if (unknown !== undefined) throw invalidParams('params.message.' + (unknown.length <= 64 ? unknown : 'field'),
        unknown === 'taskId' || unknown === 'referenceTaskIds' ? 'is not supported: this endpoint keeps no tasks' : 'is not an accepted Message field');
    if (typeof value.messageId !== 'string' || !/^[A-Za-z0-9._:-]{1,128}$/.test(value.messageId))
        throw invalidParams('params.message.messageId', 'must be 1 to 128 characters of letters, digits, ".", "_", ":" or "-"');
    if (value.role !== 'ROLE_USER') throw invalidParams('params.message.role', 'must be ROLE_USER');
    if (value.metadata !== undefined && !(record(value.metadata) && Object.keys(value.metadata).length === 0))
        throw invalidParams('params.message.metadata', 'is not supported; omit it or send an empty object');
    if (value.extensions !== undefined && !(Array.isArray(value.extensions) && value.extensions.length === 0))
        throw invalidParams('params.message.extensions', 'no extension is supported; omit it or send an empty array');
    return {input: {parts: value.parts, ...(value.contextId === undefined ? {} : {contextId: value.contextId})}, clientMessageId: value.messageId};
}

/** Handles one JSON-RPC request to `/a2a/{addressId}` from the authenticated agent `senderId`. */
export async function a2aSendMessage(env: Env, request: Request, addressId: string, senderId: string): Promise<Response> {
    let id: Id = null;
    try {
        const version = request.headers.get('A2A-Version') ?? new URL(request.url).searchParams.get('A2A-Version');
        if (version !== null && version.trim() !== A2A_VERSION)
            return failure(null, -32600, 'Version not supported', {reason: 'version-not-supported', supported: [A2A_VERSION]});
        if (!/^application\/json(?:\s*;|$)/i.test(request.headers.get('Content-Type') ?? ''))
            return failure(null, -32600, 'Invalid Request', {reason: 'Content-Type must be application/json'});
        if (Number(request.headers.get('Content-Length')) > MAX_REQUEST_BYTES) return failure(null, -32600, 'Invalid Request', {reason: 'the request is larger than 128 KiB'});
        let parsed: unknown;
        try {
            const text = new TextDecoder('utf-8', {fatal: true}).decode(await boundedBody(request, MAX_REQUEST_BYTES, () => { throw new SyntaxError(); }));
            if (!shallowJson(text, MAX_DEPTH)) throw new SyntaxError();
            parsed = JSON.parse(text);
        } catch { return failure(null, -32700, 'Parse error', {reason: 'the body must be one JSON-RPC request of at most 128 KiB, nested at most 32 levels'}); }
        if (!record(parsed)) return failure(null, -32600, 'Invalid Request', {reason: 'a single JSON-RPC request object is required; batches are not supported'});
        if (typeof parsed.id === 'string' || typeof parsed.id === 'number' || parsed.id === null) id = parsed.id as Id;
        if (parsed.jsonrpc !== '2.0' || !Object.hasOwn(parsed, 'id') || (id === null && parsed.id !== null) || typeof parsed.method !== 'string'
            || Object.keys(parsed).some(key => !['jsonrpc', 'id', 'method', 'params'].includes(key)))
            return failure(id, -32600, 'Invalid Request', {reason: 'jsonrpc "2.0", an id and a method are required; notifications are not supported'});
        if (parsed.method !== 'SendMessage') return failure(id, -32601, 'Method not found', {reason: 'only SendMessage is supported'});
        const params = parsed.params;
        if (!record(params)) throw invalidParams('params', 'must be a SendMessageRequest object');
        const extra = Object.keys(params).find(key => !['message', 'configuration', 'metadata', 'tenant'].includes(key));
        if (extra !== undefined) throw invalidParams('params.' + (extra.length <= 64 ? extra : 'field'), 'is not a SendMessageRequest field');
        if (params.tenant !== undefined && params.tenant !== '') throw invalidParams('params.tenant', 'is not supported');
        // Weave keeps no tasks: there is nothing to push, stream or return history for, so a configuration has no effect.
        if (params.configuration !== undefined && !record(params.configuration)) throw invalidParams('params.configuration', 'must be an object');
        const {input, clientMessageId} = messageInput(params.message);
        const message = checkMessage(input, 'params.message');
        // The client's messageId is the idempotency key: a retry of the same Message returns the same receipt and sends nothing new.
        // Keys share the sender's MCP `send_message` key space (the stored tool name is constrained); the hashed input names
        // this endpoint and the address, so the same key used for a different send is a conflict, never a replay.
        const claim = await idempotencyClaim('send_message', clientMessageId, {via: 'a2a', addressId, message});
        await sweep(env);
        const receipt = withoutSecrets(await atomic(env.db, async tx => {
            const recipient = await publicAddressOwner(tx, addressId);
            if (recipient === senderId) fail(400, 'invalid');
            return keyed(tx, senderId, claim, async messageId => {
                const stored = await tx.get('message', senderId, messageId);
                if (!stored) fail(404, 'unavailable');
                return terminalize(tx, stored);
            }, async () => {
                const generation = await connectedGeneration(tx, senderId, recipient);
                const payload = messagePayload(message, tx.store.now);
                return {result: await sendWithResolvedConnection(tx, senderId, recipient, generation, payload), resultId: payload.messageId};
            });
        })) as Record<string, unknown>;
        const reply = {messageId: uuidV7(BigInt(Date.now())), ...(message.contextId === undefined ? {} : {contextId: message.contextId}), role: 'ROLE_AGENT',
            parts: [{text: 'Queued for delivery to the recipient\'s Weave inbox. This does not mean the recipient has read or acted on it.'},
                {data: {weave: {status: 'queued', receipt}}, mediaType: 'application/json'}]};
        return answer(id, {result: {message: reply}});
    } catch (error) {
        if (error instanceof RpcError) return failure(id, error.rpcCode, error.reason, error.detail);
        if (error instanceof Problem) {
            const data = {status: error.status, code: error.code, ...(error.detail ?? {})};
            if (error.status === 400 && error.code === 'invalid') return failure(id, -32602, 'Invalid params', data);
            // Weave's own outcomes (not connected, unavailable, limits, storage) keep their problem code; -32000 is outside A2A's range.
            return failure(id, -32000, 'Weave refused the message', data, error.retryAfter === undefined ? {} : {'Retry-After': String(error.retryAfter)});
        }
        throw error;
    }
}
