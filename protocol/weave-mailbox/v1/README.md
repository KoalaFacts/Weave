# Weave mailbox relay v1

A separate, opt-in HTTP host transports opaque contact/inbox/outbox data over SQLite. It does not run agent reasoning, certify identities or members, decrypt payloads, or execute business work. The schema is [`mailbox.schema.json`](mailbox.schema.json); select the named `$defs` entry for each route below. This is the first protocol revision.

## Control and discovery

`X-Weave-Mailbox-Control` carries an endpoint host's secret over HTTPS. The first profile uses explicitly provisioned mailbox-control SHA-256 hashes in trusted service configuration. There is no default credential, signup, token issuer, member authentication, or identity proof. Use high-entropy secrets and protect the configuration; the mailbox ID is a control scope, not a certification. Local loopback HTTP is for tests/development. Production transport/TLS/provisioning is outside this increment.

Any supplied invalid, empty, duplicate, or revoked control header fails with 401, including on public routes. Only public card discovery can omit the header. Private cards can be exported by their owner and exchanged out of band; knowing an unlisted card ID permits a contact attempt but does not reveal its private details through discovery. One owner can publish many independently public/unlisted and long/short-lived cards. Card visibility does not accept requests.

All responses use `Cache-Control: no-store`. Requests are JSON, capped at 128 KiB, depth 16, with required fields and unknown fields rejected. Bytes are base64 and limited to 64 KiB after decoding. IDs are bounded to 128 UTF-16 code units. Generations are exact positive signed 64-bit integers; clients must preserve integer precision when parsing JSON. Method instructions, endpoints, and hints also have runtime UTF-8 byte bounds; payload encoding has a 128-character bound and excludes control characters. Methods are 1–16 per card. Nullable fields listed by the schema must be supplied explicitly as null. Do not put credentials into URLs or payload metadata.

## Routes

- GET `/v1/contacts/cards`: anonymous public, unexpired, unrevoked cards (`cardPage`)
- GET `/v1/contacts/owned-cards`: authenticated owner's eligible cards (`cardPage`)
- GET `/v1/contacts/card?cardId=<encoded ID>`: public discovery or owner export (`card`); expired/revoked cards unavailable
- PUT `/v1/contacts/cards`: owner publication/one-way revocation (`cardSubmission` → `card`); owner derived from control
- POST `/v1/contacts/requests`: opaque attempt (`contactSubmission` → `contactRelation`); requester derived from control, recipient from stored card, generation from transaction
- GET `/v1/contacts/requests`: participant-owned metadata polling (`contactPage`)
- GET `/v1/contacts/requests/{requestId}?requesterMailboxId=<encoded ID>`: participant-owned metadata (`contactSummary`)
- POST `/v1/contacts/requests/{requestId}/decision`: exact recipient decision (`contactDecision` → `contactRelation`); optional reply only appears in requester inbox
- GET `/v1/contacts/channel?peerMailboxId=<encoded ID>`: participant-relative channel generation/flags (`contactChannel`)
- PUT or DELETE `/v1/contacts/blocks`: set/clear caller's own flag using `blockSubmission` → `contactChannel`
- POST `/v1/messages`: connected peer admission (`messageSubmission` → `receipt`); sender derived from control
- GET `/v1/inbox`: owned pending bodies (`inboxPage`)
- GET `/v1/outbox`: owned body-free receipts (`outboxPage`)
- GET `/v1/outbox/{messageId}`: sender-owned body-free receipt (`receipt`)
- POST `/v1/inbox/{messageId}/ack`: recipient-controlled relay acceptance/deletion (`ackSubmission` → `receipt`); sender mailbox is supplied only in JSON
- GET `/v1/inbox/events`: authenticated SSE pending snapshots (`receivedMessage` data)

Paginated GETs accept `limit` (1–10, default 10) and opaque `afterCursor`. A cursor is scoped to owner and collection. Invalid cursor/limit is 400. Cursors never ACK; restarting a pull without a cursor replays all unacknowledged bodies. Metadata queries never contain request/reply bodies. Both participants can see pending/needs-action/accepted/rejected metadata through endpoint restarts.

Either participant's block flag fences requests and delivery through every card for that known mailbox pair. Neither participant can clear the other's flag. Block/unblock increments generation and invalidates old delivery; unblock does not reconnect or restore old bodies. A fresh contact requires an explicit recipient decision. Card expiry/revocation affects new contact establishment; an already accepted channel is independently controlled.

Arbitrary card/mailbox IDs never occupy path segments. Publish with `cardId` in JSON; discover using one `cardId` query; channel reads use one `peerMailboxId` query; blocks carry `peerMailboxId` in JSON; ACK carries `senderMailboxId` in JSON. Query fields are percent-encoded once by clients and parsed once by ASP.NET; literal percent, slash, dot segments, Unicode and reserved-looking names remain distinct. Repeated identity query fields are 400. No double decoding or older-path compatibility aliases. Only canonical lowercase UUIDs remain path IDs.

Contact identity is `(requesterMailboxId, requestId)`, not a globally unique request UUID. POST derives requester from control; two different requesters can independently submit the same timestamp-bound UUID. Same-owner identical retry stays idempotent and changed input stays 409. Participant GET supplies `requesterMailboxId` as a query locator; decision supplies it in JSON. The locator grants no authority: actual stored requester/recipient still governs access. Channel `currentRequest` is null or an object with both fields. Request lists already return both fields.

SQLite schema 1 upgrades transactionally to schema 2 on retained-file open. Request uniqueness/lookups/purge and channel/message-purpose references become requester-scoped. Migration preserves row sequences and AUTOINCREMENT high-water marks, epochs, block flags, owned retry fingerprints, payload bytes, and already-null terminal bodies. It neither resets storage nor reconstructs deleted payloads. A failed migration rolls back; startup fails closed. Existing digest layouts are retained: request/decision digests are compared only inside scoped rows, initial-message keys bind the sender/requester, and reply digests include the requester as envelope recipient. There is no global digest comparison or cross-requester replay.

## Delivery, bounds, and errors

Message IDs are UUIDv7 with the same millisecond timestamp as `createdAt`. First admission is bounded to five minutes after creation (30 seconds future skew); lifetime is positive and at most 24 hours. The same immutable ID/envelope/body retry returns the receipt; conflicting retries are 409. Terminal receipts remain seven days; old IDs cannot become fresh after purge. Payload URLs are opaque bytes and are never fetched.

Delivery is at-least-once until recipient ACK or expiry/block. ACK is relay acceptance/deletion, not human reading or business completion. Receipt states are pending, acknowledged, expired, blocked. No exactly-once business-effect guarantee. SQLite logical body removal does not imply erasure from media, historical WAL copies, or backups. Internal cleanup is bounded; there is no public maintenance route.

SSE sends `id: <message UUID>`, `event: message`, and a single JSON `data:` line. `: pending` initially flushes headers. Each poll starts a new pending traversal and each event takes a fresh owned store snapshot with effective expiry/block/generation checks. `Last-Event-ID` and SSE `afterCursor` are deliberately not replay suppression or acknowledgment. A reconnect can therefore replay earlier pending messages even after paginating/reaching the end. ACK remains available while streams are full.

The pilot has at most two streams per mailbox and 100 overall; no application event queue, one bounded emitted frame at a time (store reads one message plus one pagination lookahead), a 128-KiB Kestrel response buffer, writes bounded to ten seconds (configurable up to 30), polls no faster than once per second, and connections close after at most five minutes. Cancellation, control revocation, and write timeouts release slots without ACK. Bytes already flushed cannot be recalled by a later block; snapshot validation is not an atomic transaction with remote network delivery.

Failures return `application/problem+json` with `status`, `title`, and `code`: 400 invalid, 401 unauthenticated, 404 forbidden/unavailable (same shape to avoid cross-mailbox existence disclosure), 409 conflict, 410 expired, 413 tooLarge, 429 capacity, 503 storage. No exception details, credentials, or bodies are logged. SQLite uses short synchronous calls with cancellation checkpoints and a five-second native busy bound; a native call cannot be interrupted midway.

## Host startup

Build/run `hosts/Weave.Mailbox.Host/Weave.Mailbox.Host.csproj` with .NET 10. Configure `Mailbox__DatabasePath` and explicit `Mailbox__Controls__0__MailboxId` / `Mailbox__Controls__0__Sha256` environment variables (additional indexed controls as needed). No example secret is supplied. Existing storage is required by default; explicitly set `Mailbox__AllowCreateStorage=true` only for a new file. `ASPNETCORE_URLS` sets the listen address. Control configuration is trusted operator-only configuration and is never accepted from HTTP JSON. Revocation is an in-process trusted operator composition operation; restart with a removed credential keeps it revoked. Protect retained storage and back it up according to the operator's policy.
