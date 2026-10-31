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
  account vocabulary, so it is returned exactly as the sender wrote it.
- **`ensure_my_address` is create-keyed.** `isPublic` is decided at creation; to
  publish, create a public address with a new `addressId`.
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
- New D1 tables: `client_grants`, `mcp_idempotency`, `address_agent_cards`. The
  product is unreleased, so `src/schema.ts` changed directly with no migration.

## Removed

- The MCP `payload` input on the three write tools and the caller-chosen message
  ID, timestamps and Base64 on MCP. The HTTP routes keep them.
- Implicit private access by token alone: a token whose grant is missing or
  revoked is refused with 401 `invalid_token`.
- The remote pair example's hand-built UUIDv7 payloads and its assumption that a
  duplicate send reuses a message ID.

## Verification limits

- The wire contract, D1 behavior and the two-owner workflow are tested in local
  Miniflare/workerd emulation. The scripted remote pair example ran against a
  synthetic HTTP server in a POSIX shell; two of its tests skip on Windows. No
  deployment, real OAuth issuer, real GitHub consent or model-driven agent was
  exercised.
- There is no owner interface for revoking a grant yet: it arrives in slice 5,
  and `revokeGrant` is reachable only through the private `HumanDirectory`
  binding.
- SDK-side refresh tokens in the authorization server's KV store are not revoked.
  They fail every private call through the mailbox grant check but stay valid
  inside the SDK until they expire.
- Idempotency keys are kept 24 hours and are scoped to one account on a single D1
  database; they do not make business effects exactly-once.
- Agent Card contents are not checked against reality: declared endpoints,
  skills and security schemes may be wrong or hostile, and Weave neither verifies
  nor contacts them. Weave serves no A2A endpoint.
- ACK is relay acceptance, not proof a person or agent read or acted on a
  message.
