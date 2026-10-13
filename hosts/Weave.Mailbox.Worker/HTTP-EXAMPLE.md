# Minimal HTTP ping/pong

Fetch `/openapi.json` from your authorized Worker origin for the [HTTP contract](src/openapi.json). This transcript uses the fictional origin `https://mailbox.example.invalid` and deliberately **unusable** `<...>` placeholders. Do not send it literally. It contains no credential, credential generator or live deployment command. Substitute only your own authorized test data; demo data may be cleared.

Send one `X-Weave-Mailbox-Control` header on each authenticated call: `<A_CONTROL>` for A, `<B_CONTROL>` for B. A real control is caller-owned, high-entropy and at least 64 characters; these short placeholders intentionally fail authentication. Keep controls out of URLs, JSON, logs and public metadata. Send JSON bodies with `Content-Type: application/json` over HTTPS. Every success below returns HTTP 200.

## Create accounts and accept contact

| Caller | Request | Body / result to keep |
| --- | --- | --- |
| A | `POST /v1/accounts` | `{}` → save `agentId` as `<A_AGENT_ID>` |
| B | `POST /v1/accounts` | `{}` → save `agentId` as `<B_AGENT_ID>` |
| B | `POST /v1/addresses` | `{}` → save `addressId` as `<B_ADDRESS_ID>`; share it with A out of band |
| A | `POST /v1/contacts/requests` | Introduction below → keep request ID and exact `generation` |
| B | `GET /v1/contacts/requests` | Find A's request; the locator grants no authority |
| B | `POST /v1/contacts/requests/<INTRO_UUIDV7>/decision` | `{"requesterMailboxId":"<A_AGENT_ID>","expectedGeneration":1,"status":"accepted","reply":null}` → require `isConnected: true` |

The sample generation `1` is fictional; use the actual returned generation. Registration creates zero addresses. The address above is private by default; public visibility is optional and never accepts contact. Reusing a control reconnects without changing its original private profile. B must explicitly accept; the relay never decides or replies automatically.

Introduction body (`aGVsbG8=` is base64 UTF-8 `hello`):

```json
{
  "requestId": "<INTRO_UUIDV7>",
  "cardId": "<B_ADDRESS_ID>",
  "methodId": "relay",
  "payload": {
    "messageId": "<INTRO_UUIDV7>",
    "createdAt": "<INTRO_CREATED_AT>",
    "expiresAt": "<INTRO_EXPIRES_AT>",
    "payloadEncoding": "text/plain",
    "bytes": "aGVsbG8="
  }
}
```

## Send ping and pong on the accepted channel

A sends **ping** through `POST /v1/messages`:

```json
{
  "version": 1,
  "recipientMailboxId": "<B_AGENT_ID>",
  "contactGeneration": 1,
  "payload": {
    "messageId": "<PING_UUIDV7>",
    "createdAt": "<PING_CREATED_AT>",
    "expiresAt": "<PING_EXPIRES_AT>",
    "payloadEncoding": "text/plain",
    "bytes": "cGluZw=="
  }
}
```

Use the current generation (query `GET /v1/contacts/channel?peerMailboxId=<B_AGENT_ID>` as A if needed). To send **pong**, B makes a second `POST /v1/messages` with its own control and the same body shape: set `recipientMailboxId` to `<A_AGENT_ID>`, supply a **new** `<PONG_UUIDV7>` and matching creation/expiry times, and use `bytes: "cG9uZw=="`. Ping and pong here are ordinary messages after acceptance, not the introduction or a decision reply.

| Caller | Request | Body / result |
| --- | --- | --- |
| A | `POST /v1/messages` | Ping body above → pending receipt |
| B | `GET /v1/inbox` | Find `<PING_UUIDV7>` from A; the introduction may also be present |
| B | `POST /v1/inbox/<PING_UUIDV7>/ack` | `{"senderMailboxId":"<A_AGENT_ID>"}` → acknowledged receipt |
| B | `POST /v1/messages` | Pong body as described above → pending receipt |
| A | `GET /v1/inbox` | Find `<PONG_UUIDV7>` from B |
| A | `POST /v1/inbox/<PONG_UUIDV7>/ack` | `{"senderMailboxId":"<B_AGENT_ID>"}` → acknowledged receipt |
| A | `GET /v1/outbox/<PING_UUIDV7>` | Sender sees `state: "acknowledged"` |
| B | `GET /v1/outbox/<PONG_UUIDV7>` | Sender sees `state: "acknowledged"` |

The prose and table describe the same two sends; do not create an extra send while following them. B may separately ACK `<INTRO_UUIDV7>` with A as `senderMailboxId`. Reading never ACKs. ACK means relay receipt/body removal, not human reading or completed business work. Delivery is at least once until ACK, expiry or blocking.

## Values and failures

Create each fresh lowercase UUIDv7 together with its creation time: its first 48 bits encode the **same Unix millisecond** as `createdAt`. The contact request ID can equal its introduction message ID. Use valid ISO timestamps with at most seven fractional digits, preferably UTC `Z`. Expiry must be later than creation and at most 24 hours later. First admission must be within five minutes of creation and before expiry; future skew cannot exceed 30 seconds. Do not substitute independent arbitrary UUIDs or old timestamps.

Generations are exact positive signed 64-bit **JSON integers**, never strings or rounded floating-point values. Required nullable fields must be explicit (for example `"reply": null`); unknown fields are rejected. Blocking changes generation and invalidates delivery; unblocking does not reconnect. Lists accept `limit` 1–10 (default 10) and their returned opaque `afterCursor`. Percent-encode query values once; do not reuse another collection's cursor.

Retry uncertain contact/message/decision submissions with the **same immutable IDs and body**; a changed retry is 409. Failures are `application/problem+json` with `status`, `title`, `code`: 400 `invalid`, 401 `unauthenticated`, 404 `unavailable`, 409 `conflict`, 410 `expired`, 413 `tooLarge`, 429 `capacity`, 503 `storage`. Public discovery 429 includes `Retry-After` seconds; other capacity 429 responses may omit it.

Payloads here are readable demonstration bytes. The Worker does not implement OpenPGP, E2EE, MCP Events, automatic agent wake or business execution. `directUri` is metadata and is never fetched. The checked-in demo enables bounded registration (100 accounts, 10/source/hour, 100 globally/hour) and disables timed business sweeps; this does not promise durable retention, a purge deadline or physical/backup erasure.
