# Retrying a proposal safely: `Idempotency-Key` on external actions

## Scope

Builds on [governed external actions](2026-11-04-worker-external-actions.md) and the
[single-action approval page](2026-11-05-action-approval-page.md). Requested by Envoylet on the coordination issue
(`propose-idempotency`).

If `POST /v1/external-actions` gets no response (a timeout or a reset connection), the agent cannot tell whether a
plan was recorded. Before this change a retry created a second pending plan that the owner would also see, and a
client that did not retry left an orphan plan it could neither show nor execute.

An agent may now send an optional key with its proposal:

- HTTP: the `Idempotency-Key` request header on `POST /v1/external-actions`;
- MCP: the `idempotencyKey` argument of `propose_external_action`.

Both name the same per-agent key space, so a key first used over MCP replays over HTTP and the other way round.

## Behaviour

- **Key format.** 1 to 128 characters of letters, digits, `.`, `_`, `:` and `-`, as MCP `idempotencyKey` elsewhere.
  Anything else is `400 invalid` with `field: "Idempotency-Key"` (HTTP) or `"idempotencyKey"` (MCP), before any
  write. Two header values arrive joined by `, ` and are refused the same way.
- **Same key, same request.** The request is the credential reference, the operation and the full content. A repeat
  returns the action the key first created, in its current state, exactly as `GET /v1/external-actions/{id}` would
  show it: pending with its `approvalUrl`, or since approved, rejected, expired or executed. Nothing is written. The
  replay reads the action; it does not re-check the credential, the content rules or the 20-pending capacity, so a
  credential revoked after the first proposal still returns the original action.
- **Same key, different request.** `409 conflict` with `field: "action"` and `reason: "idempotency-conflict"`.
  Nothing is written.
- **Scope.** Keys belong to the agent. Another agent using the same key gets its own action and can never read the
  first agent's through it.
- **Retention.** A key is kept as long as the action it names, which Weave keeps. It never becomes reusable for a
  different proposal. This is longer than the 24 hours of the MCP message keys (`mcp_idempotency`), because a plan
  that may still be approved and executed must not be confused with a new one.
- **Without a key.** Unchanged: every proposal is a new plan.
- **Authority.** A replay still requires the caller's live authority: the DPoP-bound key for HTTP, the consent grant
  for MCP. Maintenance refuses it like any write. The key is not part of the plan digest and the owner never sees
  it; approval, at-most-once dispatch and `unknown` handling are unchanged.

## Storage

One new table, `external_action_keys`: `(account_id, idempotency_key)` primary key, `request_hash` (SHA-256 of the
credential reference, operation and content), `action_id` (unique, references `external_actions`) and `created_at`.
The key row is written in the same atomic batch as the action. Two concurrent proposals with the same key are
serialized by the mailbox revision guard: the second retries, finds the key and returns the first action.

The schema change is additive (a new table and index), so the release's schema plan creates it without touching
existing tables. No data migration.

## Verification

mailbox-worker tests (real D1 through Miniflare; MCP through the bundled private-tool module):

- the same key and body return the identical action, and the owner page lists one plan;
- after approval and a credential revocation, the same key returns the original action as `approved`, with
  `approvalUrl` null, and no new row;
- a different content or a different credential under the same key is `409 idempotency-conflict` and writes nothing;
- keys are per agent, another agent's action stays 404, and proposals without a key still create separate plans;
- malformed keys (space, 129 characters, a joined `k1, k2`, `/`) are `400` naming `Idempotency-Key`; a 128-character
  key is accepted; an empty value is refused by the parser (an empty header does not reach the Worker in Miniflare);
- three concurrent proposals with one key create one plan;
- a key row relinked to another agent's account (both foreign keys still valid) fails closed with `503 storage` instead
  of returning the other agent's action;
- MCP: the same `idempotencyKey` twice returns the same action, the HTTP header with that key returns it too, changed
  content conflicts and an invalid key is 400.

Before the change, the replay, conflict and invalid-key tests failed (a second plan, `200` instead of `409`, `200`
instead of `400`); the per-agent test is the positive control and passed.

Mutation checks: each of these fails at least one test:

- replaying without comparing the request hash;
- not recording the key row;
- looking the key up without the agent's account;
- accepting a key without checking its pattern;
- ignoring the HTTP header;
- ignoring the MCP argument;
- re-checking the credential on replay.
- dropping the replayed action's account check.

## Limits

- No list endpoint (`GET /v1/external-actions?status=pending`); the key is the reconciliation path.
- The key identifies a proposal, not an execution. `execute` was already safe to repeat: it never re-sends a
  dispatched action.
- Envoylet's demo check (propose with a key, drop the response, repeat, see one plan on `/account`) needs the owner's
  deploy of this change.
