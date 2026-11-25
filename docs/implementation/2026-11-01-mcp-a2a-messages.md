# MCP text messages, revocable grants and Agent Cards — 2026-11-01

This record covers onboarding slice 4 for the TypeScript Worker relay in
`typescript/mailbox-worker` and the consent step in `typescript/auth-worker`. A
person who connected an agent through OAuth consent can run the first workflow
over `/mcp` with plain text, publish a public address with an A2A Agent Card,
and lose access on the next private call once the grant is revoked. It is source
and local Worker/D1/workerd verification. **Nothing has been deployed**; the
public demo still runs an earlier release, and the documented commands were
source-checked, not run against a deployment.

The wire contract is in the Worker [README](../../typescript/mailbox-worker/README.md),
the [OpenAPI contract](../../typescript/mailbox-worker/src/openapi.json), the
[protocol README](../../protocol/weave-mailbox/v1/README.md#a2a-alignment-worker-relay-profile)
(which pins A2A v1.0.0) and the program guide
[`agent-start.md`](../../typescript/mailbox-worker/src/agent-start.md). It is
not repeated here. The preceding slice is the
[agent binding record](2026-11-01-agent-binding.md).

## Preserved

- The HTTP `/v1` payload contract: callers still send any payload, encoding and
  message ID, and nothing there changed shape.
- Agent-key authentication, DPoP tokens, and the per-call `validateToken` check
  of scope, audience and expiry on every private MCP call.
- Delivery semantics: at-least-once until ACK or expiry, retained receipts, and
  the `outcome_unknown` rule that a retry reuses the original intent.
- Inbox content is untrusted peer data, never a server notice or authority to run
  tools.
- Slice 3 public-address limits and the create-keyed `ensure_my_address`.

## Deliberately changed

- **Server-built messages.** The MCP write tools (`request_contact`,
  `decide_contact` reply, `send_message`) take
  `message: {parts: [{text}|{data: object}], contextId?}`. The server builds the
  A2A v1.0.0 Message (`ROLE_USER`, UUIDv7 `messageId`, `application/json`
  payload); the envelope `createdAt` is the UUIDv7 millisecond and `expiresAt`
  is 24 hours later. `Part.data` must be a JSON object, narrower than A2A, which
  allows any JSON value. At most 8 parts and 64 KiB serialized; a `data` object
  nests at most 16 levels (the object itself is level 1), checked iteratively
  before any write.
- **Idempotency keys (MCP only).** `idempotencyKey` is per account, bound to the
  tool and the validated input, and kept 24 hours. A replay returns the
  *current* result for the original message or request, not a frozen copy;
  different input is the MCP-only tool error `idempotency-conflict`; without a
  key a retry may duplicate. `send_message` may omit `contactGeneration`; with no
  accepted channel the MCP-only tool error is `not-connected`.
- **Inbox.** `list_my_inbox` shows a parsed `message` and omits `bytes` only when
  `payloadEncoding` is `application/json`, the body has the Message shape and its
  `messageId` equals the envelope's. The body is parsed only if it nests at most
  32 levels and holds at most 10 000 JSON values (a bracket-depth scan before
  parsing, an iterative walk after) and every part is an object, so a peer's
  deeply nested body cannot fail the recipient's inbox page. Anything else keeps
  `bytes`. The parsed `message` is attached after server data is renamed to
  account vocabulary and is spliced in as the peer's own JSON text, so it is
  returned exactly as the sender wrote it. In the MCP result the text content
  carries those exact bytes; `structuredContent` holds the
  same `message` as plain parsed JSON, so peer numbers there are JavaScript numbers
  (an integer beyond 2^53 is rounded) and peer keys named `generation` are not
  turned into decimal strings, unlike the server's own generation fields.
- **`ensure_my_address` is create-keyed.** `isPublic` is decided at creation; to
  publish, create a public address with a new `addressId`. (Until 2026-11-24:
  `update_my_address` now edits an existing address; see
  [below](#editing-an-address-over-mcp-2026-11-24).)
- **Agent Cards.** `publish_my_agent_card` attaches an A2A Agent Card subset to a
  public address the caller owns. It is agent-declared and never verified
  (`agentCardVerified: false`), accepts only normalized `https://` URLs and is
  served anonymously at `GET /v1/directory/address/{addressId}/agent-card.json`
  and as `agentCard` in public directory views. It is deleted when the address is
  revoked or made private and is not served outside the address's effective
  window. Weave never fetches a card URL.
- **Revocable grants.** Each consent grant is mirrored in D1 (`client_grants`)
  before the SDK issues an authorization code; if that fails, consent returns 503
  and issues no code. `HumanDirectory` gains `recordGrant` and `revokeGrant`.
  Every private MCP call requires the token's grant row to be unrevoked and to
  match account, human and client, so a revocation takes effect on the next call,
  including with a refreshed token, which keeps its `grantId`. The grant is a
  single point read per call, checked before scope (a revoked token is 401 even
  without the scope). Every write tool re-reads the grant inside its own
  transaction, so a revocation committed between authorization and the write
  stops it: the tool error is `grant-revoked` (HTTP 401 inside the tool result)
  and nothing is written.
- **RFC 7662 introspection mode refuses private tools.** An introspection response
  cannot carry a `grantId`, so that mode still validates tokens but never
  authorizes a private call. The remote pair example now needs grant-bearing
  tokens and uses the new `message` / `idempotencyKey` contract.
  *2026-11-25: the introspection mode is deleted; the Service Binding is the only
  mode (owner decision on #365).*
- New D1 tables: `client_grants`, `mcp_idempotency`, `address_agent_cards`. The
  product is unreleased, so `src/schema.ts` changed directly with no migration.

## Removed

- The MCP `payload` input on the three write tools and the caller-chosen message
  ID, timestamps and Base64 on MCP. The HTTP routes keep them.
- Implicit private access by token alone: a token whose grant is missing or
  revoked is refused with 401 `invalid_token`.
- The remote pair example's hand-built UUIDv7 payloads and its assumption that a
  duplicate send reuses a message ID.

## Blocking over MCP (2026-11-22)

`set_contact_block {peerAccountId, expectedGeneration, blocked}` (`weave:contacts.write`, idempotent) gives MCP agents what `PUT`/`DELETE /v1/contacts/blocks` already gave HTTP agents. It runs the same `setBlocked` in the consent grant's guarded transaction and returns the `get_my_channel` shape. Blocking ends any pending request, advances the generation (so messages still pending on the old generation end `blocked`), and refuses messages and contact requests in both directions; unblocking advances the generation again and restores no contact. The same state again returns the channel unchanged; an old generation is `409 conflict`; no channel with the peer is `404 unavailable`; a bad argument is a field-level `400 invalid` (`generation` for a malformed `expectedGeneration`, as on `decide_contact`); a revoked grant is `401` with nothing changed.

Verification: `test/mcp-messages.test.ts` `setContactBlock_BlockThenUnblock_StopsMessagesAndNeedsANewRequest` and `setContactBlock_InvalidStrangerOrRevokedGrant_RefusedWithoutChange` (bundled production module, real D1 via Miniflare); `test/mcp-tool-contract.test.ts` checks both results against the published output schema. Mutations, each caught: the grant guard removed, the boolean check removed, `blocked` forced to true, extra arguments accepted, the read scope instead of the write scope, the peer UUID check removed.

## Removing a contact over MCP (2026-11-24)

`remove_contact {peerAccountId}` (`weave:contacts.write`, idempotent) gives MCP agents what `DELETE /v1/contacts/accounts/{accountId}` already gave HTTP agents. It runs the same removal in the consent grant's guarded transaction and returns `{peerAccountId, removed}`. Only the caller's own contact record goes; the peer's record and all request history stay. The channel generation advances, so messages still pending in either direction are not delivered, a pending request ends and neither side can send until a new contact request is accepted. With no contact record for that peer (a repeat, or a stranger) it returns `removed: false` and changes nothing. A bad argument, an extra argument or the caller's own account is a field-level `400 invalid` on `peerAccountId` (or the extra field); a revoked grant is `401` with nothing changed. HTTP is unchanged and still answers `{}`.

Verification: `test/mcp-messages.test.ts` `removeContact_ConnectedPeer_EndsDeliveryUntilANewRequest` and `removeContact_InvalidSelfStrangerOrRevokedGrant_RefusedWithoutChange` (bundled production module, real D1 via Miniflare); `test/mcp-tool-contract.test.ts` checks the result against the published output schema. Mutations are listed in the PR.

## Revoking an address over MCP (2026-11-24)

`revoke_my_address {addressId}` (`weave:addresses.write`, idempotent) gives MCP agents what `DELETE /v1/addresses/{addressId}` already gave HTTP agents: before it, an MCP agent could create a public address but never take it out of the directory. It runs the same revocation in the consent grant's guarded transaction and returns `{addressId, revoked}`. The address leaves the directory at once, its Agent Card is deleted in the same transaction, and it can never be recreated or take a new card; its ID, owner and revocation time stay. Contacts and messages are not touched. When the caller had already revoked it, it returns `revoked: false` and changes nothing (the first revocation time stays), so a retry after a lost answer is safe; HTTP still answers a second `DELETE` with `404`. Another agent's or an unknown address is `404 unavailable`, a bad or extra argument is a field-level `400 invalid`, and a revoked grant is `401` with nothing changed. It is the first private tool published with `destructiveHint: true`, so MCP clients can ask before a revocation that cannot be undone.

Verification: `test/agent-cards.test.ts` `revokeMyAddress_OwnPublicAddressWithCard_LeavesDirectoryForGoodAndRepeatChangesNothing` and `revokeMyAddress_InvalidOtherAgentsUnknownOrRevokedGrant_RefusedWithoutChange` (bundled production module, real D1 via Miniflare); `test/mcp-tool-contract.test.ts` checks the result against the published output schema. Mutations are listed in the PR.

## Verification limits

- The wire contract, D1 behavior and the two-owner workflow are tested in local
  Miniflare/workerd emulation. The scripted remote pair example ran against a
  synthetic HTTP server in a POSIX shell; two of its tests skip on Windows. No
  deployment, real OAuth issuer, real GitHub consent or model-driven agent was
  exercised.
- Owner revocation of a grant and the SDK refresh-token clean-up arrived in
  slice 5; see the [owner grants record](2026-11-01-owner-grants.md) for its
  behavior and limits.
- Idempotency keys are kept 24 hours and are scoped to one account on a single D1
  database; they do not make business effects exactly-once.
- Agent Card contents are not checked against reality: declared endpoints,
  skills and security schemes may be wrong or hostile, and Weave neither verifies
  nor contacts them. Weave serves no A2A endpoint.
- ACK is relay acceptance, not proof a person or agent read or acted on a
  message.

## Editing an address over MCP (2026-11-24)

`update_my_address {addressId, label, isPublic, effectiveFrom, effectiveTo, directUri}` (`weave:addresses.write`, idempotent, `destructiveHint: true`) gives MCP agents what `PUT /v1/addresses/{addressId}` already gave HTTP agents: before it, `ensure_my_address` was create-keyed, so an MCP agent could neither publish an unlisted address, unlist a public one, nor change its label, effective window or direct URI. Like `PUT`, it replaces every setting: all six arguments are required and `null` clears a nullable one, so an omitted field is never silently cleared. It runs `mutateAddress`, the `PUT` path, in the consent grant's guarded transaction, with the same checks (label ≤ 128 characters, UTC times ending in `Z` with `effectiveTo` after `effectiveFrom`, `http(s)` `directUri` without credentials) and the same public-address limit (`429 capacity`) when an unlisted address becomes public. Unlisting removes the address from the directory and deletes its Agent Card in the same transaction; making it public again does not bring the card back, hence `destructiveHint`. Sending the current settings again writes nothing. A revoked, another agent's or an unknown address is `404 unavailable`; a missing, extra or wrongly typed argument is a field-level `400 invalid`; a revoked grant is `401` with nothing changed. As with `PUT`, the write's answer echoes the times as sent while reads return them normalized to milliseconds (`…T00:00:00.000Z`); this pre-existing difference is unchanged.

Verification: `test/agent-cards.test.ts` `updateMyAddress_OwnAddress_ReplacesSettingsLikePutAndUnlistingDeletesTheCard` and `updateMyAddress_InvalidOtherAgentsRevokedUnknownOverLimitOrRevokedGrant_RefusedWithoutChange` (bundled production module, real D1 via Miniflare); `test/mcp-tool-contract.test.ts` checks the result against the published output schema. Mutations are listed in the PR.

## Reading one receipt over MCP (2026-11-24)

`get_my_receipt {messageId}` (`weave:messages.read`, read-only) gives MCP agents what `GET /v1/outbox/{messageId}` already gave HTTP agents: before it, an MCP agent that sent a message had to page `list_my_outbox` from the start to learn whether that one message was acknowledged, expired or blocked. It runs the same sender-owned `read` as the HTTP route inside the consent grant's guarded transaction and returns the receipt `list_my_outbox` lists (`messageId`, `senderAccountId`, `recipientAccountId`, `state`, `expiresAt`, `terminalAt`). Only the sender reads it: the recipient, another agent or an unknown ID gets `404 unavailable`; a missing, malformed or extra argument is a field-level `400 invalid`; a revoked grant is `401`.

Verification: `test/mcp-messages.test.ts` `getMyReceipt_SenderReadsOwnReceipt_OthersUnknownMalformedOrRevokedRefused` (bundled production module, real D1 via Miniflare); `test/mcp-tool-contract.test.ts` checks the result against the published output schema. Mutations are listed in the PR.

## Reading one contact request over MCP (2026-11-24)

`get_my_contact_request {requestId, requesterAccountId}` (`weave:contacts.read`, read-only) gives MCP agents what `GET /v1/contacts/requests/{requestId}?requesterAccountId=` already gave HTTP agents: before it, an MCP agent that sent or received an introduction had to page `list_my_contact_requests` from the start to learn whether that one request was accepted, rejected or expired. Request IDs are requester-scoped, so the requester is named too. It runs the same `read` as the HTTP route inside the consent grant's guarded transaction and returns the summary `list_my_contact_requests` lists. Only the requester and the recipient read it: a third agent, the wrong requester or an unknown ID gets `404 unavailable`; a missing, malformed or extra argument is `400 invalid` (a malformed ID names its field); a revoked grant is `401`.

Verification: `test/mcp-messages.test.ts` `getMyContactRequest_RequesterAndRecipientRead_OthersUnknownMalformedOrRevokedRefused` (bundled production module, real D1 via Miniflare); `test/mcp-tool-contract.test.ts` checks the result against the published output schema; the auth-worker workerd test `consent_GithubSignedInAgents_ExchangeSixMessagesAcrossColdRestart` reads one request through a real consent token and is refused for the wrong requester. Mutations are listed in the PR.

## Reading one address over MCP (2026-11-25)

`get_my_address {addressId}` (`weave:addresses.read`, read-only) gives MCP agents what `GET /v1/addresses/{addressId}` already gave HTTP agents: before it, an MCP agent had to page `list_my_addresses` to read one of its own addresses, for example after `update_my_address` or before publishing an Agent Card. It runs the same owner-only `ownedAddresses` read as the HTTP route inside the consent grant's guarded transaction and returns the view `list_my_addresses` lists. Only live addresses are read (the HTTP `includeRevoked` history is not offered); a revoked, another agent's or an unknown address is `404 unavailable`; a missing, malformed or extra argument is a field-level `400 invalid`; a revoked grant is `401`.

Verification: `test/agent-cards.test.ts` `getMyAddress_OwnLiveAddress_ReadLikeGet_OthersRevokedUnknownMalformedOrRevokedGrantRefused` (production module bundled, real D1 via Miniflare); `test/mcp-tool-contract.test.ts` checks the result against the published output schema. Mutations caught: dropping the grant guard, accepting an extra argument, skipping the UUID check.

## A2A SendMessage to a public address (2026-11-25)

Roadmap #586, step R1-1 (#585): an agent that speaks A2A v1.0 can send to a Weave public address without learning `/v1`. `POST /a2a/{addressId}` serves the JSON-RPC 2.0 binding of `SendMessage` (method names, `SendMessageRequest`/`SendMessageResponse` and enum names checked against `specification/a2a.proto` and `docs/whats-new-v1.md` on the A2A `main` branch on 2026-11-25; the v1.0.0 tag pages could not be fetched from the sandbox). The caller authenticates like `/v1` and must be an accepted, unblocked contact of the address's owner; the send is the `send_message` path (`connectedGeneration`, `sendWithResolvedConnection`, the hourly send limit) inside one transaction, with the client's `messageId` as the idempotency key in the `send_message` key space (the stored tool name is constrained, and the hashed input names this endpoint and the address, so a shared key conflicts rather than replays). The answer is always a `ROLE_AGENT` Message with the receipt; no Task is created.

Deliberately not in R1-1: an unknown sender becoming a contact request (R1-2), Agent Cards advertising the interface (R1-3), the HTTP+JSON and gRPC bindings, streaming, push notifications and any task operation.

Verification: `test/a2a-endpoint.test.ts` (built Worker, real D1 via Miniflare): an accepted contact's Message is stored as a server-built A2A Message and answered "queued" without a task or channel token; a retry with the same `messageId` returns the same receipt with one delivery and changed content conflicts; not connected, blocked, private, unknown and own addresses are refused with nothing stored; no credential is HTTP 401; parse, batch, notification, method, version and field errors are JSON-RPC errors naming the field. Mutations caught: dropping the idempotency claim, accepting `ROLE_AGENT`, allowing a send to one's own address, ignoring `A2A-Version`.

Follow-up (Codex finding on #588): a retry after the recipient blocked the sender or acknowledged the message answered "queued". The answer now follows the receipt: `queued` while pending, `acknowledged`, or `not-delivered` (blocked or expired). `a2aSendMessage_RetryAfterBlockOrAck_ReportsTheReceiptAsItStands` fails when the status is fixed to `queued`.
