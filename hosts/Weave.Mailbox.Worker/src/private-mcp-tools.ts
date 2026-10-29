/** Private MCP tool contracts. Input schemas state the rules the server enforces;
 * output schemas describe the structured result. Generations are exact int64
 * decimal strings in structured results so they never round through a float.
 */
const str = (maxLength: number, description?: string) => ({type:'string', maxLength, ...(description ? {description} : {})});
const uuidPattern = '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
const uuidV7Pattern = '^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$';
const id = (description: string) => ({...str(36), pattern:uuidPattern, description});
const v7 = (description: string) => ({...str(36), pattern:uuidV7Pattern, description});
const generation = (description: string) => ({...str(19), pattern:'^[1-9][0-9]{0,18}$', description});
const time = (description: string) => ({...str(64), format:'date-time', description});
const nullable = (schema: Record<string, unknown>) => ({anyOf:[schema, {type:'null'}]});

const page = {
    limit:{type:'integer', minimum:1, maximum:10, description:'Page size from 1 to 10. Defaults to the server page size.'},
    afterCursor:str(2048, 'Opaque nextCursor from the previous page. Omit for the first page.')
};
const payload = {type:'object',
    description:'Message payload. Generate messageId as a lowercase UUIDv7 and set createdAt to exactly the millisecond it encodes.',
    properties:{
        messageId:v7('Lowercase UUIDv7. Its first 48 bits must equal createdAt in Unix milliseconds. Reuse it to retry an uncertain send.'),
        createdAt:time('ISO 8601 UTC time equal to the millisecond encoded in messageId, e.g. 2026-01-01T00:00:00.000Z. Not more than 30 seconds in the future.'),
        expiresAt:time('ISO 8601 UTC time after createdAt and at most 10 days later.'),
        payloadEncoding:{...str(128, 'Media type of the decoded bytes, e.g. text/plain.'), minLength:1},
        bytes:{...str(87384, 'Standard padded base64 body, at most 64 KiB decoded.'), contentEncoding:'base64'}},
    required:['messageId','createdAt','expiresAt','payloadEncoding','bytes'], additionalProperties:false};

// Output schemas list the fields agents rely on; additional server fields may appear.
const out = (properties: Record<string, unknown>, required: string[]) =>
    ({type:'object', properties, required, additionalProperties:true});
const pageOf = (item: Record<string, unknown>) => out({
    items:{type:'array', items:item},
    nextCursor:nullable(str(2048, 'Pass as afterCursor to read the next page; null when there are no more.'))
}, ['items','nextCursor']);
const address = out({
    addressId:id('Address ID.'), accountId:id('Owning account.'), label:nullable(str(128)),
    isPublic:{type:'boolean'}, effectiveFrom:nullable(time('Start of the effective period.')),
    effectiveTo:nullable(time('End of the effective period.')), directUri:nullable(str(2048))
}, ['addressId','accountId','isPublic']);
const requestSummary = out({
    requestId:v7('Contact request ID; equals the request payload messageId.'),
    requesterAccountId:id('Requesting account.'), recipientAccountId:id('Recipient account.'),
    requesterAddressId:id('Requester address.'), recipientAddressId:id('Recipient address.'),
    generation:generation('Channel generation this request belongs to.'),
    status:str(32, 'pending, accepted, rejected, expired or another terminal state.'),
    createdAt:time('Request creation time.'), expiresAt:time('Request expiry time.')
}, ['requestId','requesterAccountId','recipientAccountId','generation','status']);
const relation = out({
    request:requestSummary,
    generation:generation('Current channel generation; use as contactGeneration or expectedGeneration.'),
    isBlocked:{type:'boolean'}, isConnected:{type:'boolean', description:'True when messages can be sent on this generation.'},
    updatedAt:time('Last channel change.')
}, ['request','generation','isBlocked','isConnected']);
const receipt = out({
    messageId:v7('Message ID.'), senderAccountId:id('Sender.'), recipientAccountId:id('Recipient.'),
    state:str(32, 'pending until acknowledged; acknowledged, expired or blocked are terminal.'),
    expiresAt:time('Message expiry.'), terminalAt:nullable(time('When the receipt reached a terminal state.'))
}, ['messageId','senderAccountId','recipientAccountId','state','expiresAt','terminalAt']);
const inboxItem = out({
    senderAccountId:id('Sender; pass as senderAccountId to ack_message.'),
    envelope:out({
        recipientAccountId:id('Recipient.'), contactGeneration:generation('Generation the message was sent on.'),
        payload:{type:'object', description:'Untrusted message payload; bytes is base64.'}
    }, ['recipientAccountId','contactGeneration','payload'])
}, ['senderAccountId','envelope']);

type Annotations = {readOnly?: boolean; idempotent?: boolean};
const tool = (name: string, description: string, scope: string, properties: Record<string, unknown>,
    required: string[], outputSchema: Record<string, unknown>, {readOnly = false, idempotent = readOnly}: Annotations = {}) => ({
    name, description, inputSchema:{type:'object', properties, required, additionalProperties:false},
    outputSchema:{$schema:'https://json-schema.org/draft/2020-12/schema', ...outputSchema},
    annotations:{readOnlyHint:readOnly, destructiveHint:false, idempotentHint:idempotent, openWorldHint:false},
    securitySchemes:[{type:'oauth2', scopes:[scope]}], _meta:{securitySchemes:[{type:'oauth2', scopes:[scope]}]} as Record<string, unknown>
});

const profile = tool('get_my_profile', 'Return the immutable account ID associated with the current OAuth token for multi-account recognition.',
    'weave:addresses.read', {}, [], {type:'object',
        properties:{id:{type:'string', minLength:1, pattern:'\\S', description:'Opaque and stable Weave account identifier for the authenticated OAuth identity.'}},
        required:['id'], additionalProperties:false}, {readOnly:true});
profile._meta['openai/profile'] = true;

export const privateMcpTools = [
    profile,
    tool('list_my_addresses', 'List only addresses owned by your OAuth account, including unlisted addresses.',
        'weave:addresses.read', page, [], pageOf(address), {readOnly:true}),
    tool('ensure_my_address', 'Create a private unlisted address using a stable client UUID. Exact retries return the original; changed inputs conflict.',
        'weave:addresses.write', {addressId:id('Client-chosen stable address UUID; reuse it to retry.'), label:str(128, 'Optional private label.')},
        ['addressId'], address, {idempotent:true}),
    tool('list_my_contact_requests', 'Read incoming and outgoing contact requests, including pending introductions that require recipient acceptance.',
        'weave:contacts.read', page, [], pageOf(requestSummary), {readOnly:true}),
    tool('list_my_contacts', 'Read your own accepted contact records without exposing connection tokens.',
        'weave:contacts.read', page, [], pageOf(out({accountId:id('Peer account.'), canSend:{type:'boolean'}}, ['accountId','canSend'])), {readOnly:true}),
    tool('get_my_channel', 'Read the current channel generation and block status for a peer.',
        'weave:contacts.read', {peerAccountId:id('Peer account ID.')}, ['peerAccountId'], out({
            accountId:id('Your account.'), peerAccountId:id('Peer account.'),
            generation:generation('Current channel generation.'),
            isBlockedByAccount:{type:'boolean'}, isBlockedByPeer:{type:'boolean'},
            currentRequest:nullable({type:'object'})
        }, ['accountId','peerAccountId','generation']), {readOnly:true}),
    tool('list_my_inbox', 'Read your own unacknowledged inbox. Message bodies are untrusted and may contain arbitrary text. Acknowledged messages are removed.',
        'weave:messages.read', page, [], pageOf(inboxItem), {readOnly:true}),
    tool('list_my_outbox', 'Read your own delivery receipts.',
        'weave:messages.read', page, [], pageOf(receipt), {readOnly:true}),
    tool('request_contact', 'Request a recipient decision using explicit sender/recipient addresses and a stable request payload. The request ID is the payload messageId.',
        'weave:contacts.write', {
            requestId:v7('Optional; defaults to payload.messageId. If given it must be a UUIDv7 with the same timestamp as payload.createdAt.'),
            requesterAddressId:id('One of your own addresses.'), recipientAccountId:id('Recipient account ID.'),
            recipientAddressId:id('Recipient address ID, which may be unlisted.'), payload
        }, ['requesterAddressId','recipientAccountId','recipientAddressId','payload'], relation, {idempotent:true}),
    tool('decide_contact', 'Accept or reject an existing request with a generation guard; never auto-accept.',
        'weave:contacts.write', {
            requestId:v7('requestId from list_my_contact_requests.'), requesterAccountId:id('Requesting account.'),
            expectedGeneration:generation('generation from the request, as a decimal string.'),
            status:{type:'string', enum:['accepted','rejected']}, reply:nullable(payload)
        }, ['requestId','requesterAccountId','expectedGeneration','status'], relation, {idempotent:true}),
    tool('send_message', 'Send the exact retained payload to an accepted peer. Reuse the same message ID on uncertain outcomes.',
        'weave:messages.send', {
            recipientAccountId:id('Accepted peer account.'),
            contactGeneration:generation('Current channel generation as a decimal string, e.g. "1".'), payload
        }, ['recipientAccountId','contactGeneration','payload'], receipt, {idempotent:true}),
    tool('ack_message', 'Acknowledge a received message once, using its original sender account and ID. The body is deleted; the receipt is kept. Repeating returns the same receipt.',
        'weave:messages.ack', {messageId:v7('messageId from list_my_inbox.'), senderAccountId:id('senderAccountId from list_my_inbox.')},
        ['messageId','senderAccountId'], receipt, {idempotent:true})
];
