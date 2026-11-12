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

The schema change is additive (a new table and index) and needs no data migration. It still cannot ship through the
current release: both deploy jobs run `completed-only`, which refuses any non-empty schema plan. The owner approved the
table on 2026-11-11; rolling it out still needs a separate protected, opt-in path that applies exactly these two
statements, and an owner deploy.


### Owner decision and release path

- 2026-11-11: the owner approved `schema-366`, the additive `external_action_keys` table and its unique index. The
  feature merges to main without being deployed. Applying the table to production D1 is still an owner-authorized
  deploy.
- The opt-in deploy guard drafted on #366 (`WEAVE_APPROVED_EXTERNAL_ACTION_KEYS_SCHEMA`, commits `cd1fd00` and
  `7c88e6e`) is not part of this merge. It is release-path code with two open review findings (compare full constraint
  definitions, not names; test a successful opted-in deploy at the D1 boundary) and moves to the separate
  additive-schema release-path PR.
- 2026-11-12: the owner authorized applying the table to production D1. The release path below delivers it, with both
  findings fixed. The opt-in is still set explicitly on each run; nothing sets it by default.

### Release path: applying the table to production (2026-11-12)

**How a release compares the schema.** `scripts/deploy.ts` connects to the live D1 through Wrangler's remote binding.
In `completed-only` mode (every ordinary release) `scripts/completed-deployment.ts` hands the planner a metadata-only
database and `planLatestSchema` compares the live tables and indexes with a fresh in-memory database generated from
`src/schema.ts`: missing objects become `CREATE` statements, and changed columns, foreign keys, CHECKs or indexes stop
the release. `completed-only` requires that plan to be empty at preflight, before the ledger check, after it and before
activation, so since #421 it refuses every release until the table exists.

**The opt-in.** `WEAVE_APPROVED_EXTERNAL_ACTION_KEYS_SCHEMA=true`, set from the `apply_external_action_keys_schema`
input of a manual production dispatch of [Deploy Weave Worker](../../.github/workflows/deploy-weave-worker.yml)
(default `false`; the owner comment on #236, `publish/demo` and repository scope can never set it). With it, the
completed-only release (`scripts/approved-external-action-keys.ts`):

- accepts a pending plan only if every statement, in order, equals one of the two pinned approved statements as whole
  normalized DDL (every column, type, default, constraint expression, foreign-key action, index column and
  `UNIQUE`), not just their names; anything else pending is refused before any upload or write (finding P2);
- refuses an existing `external_action_keys` table or `external_action_keys_action` index whose full definition differs,
  and any other index or trigger on the table;
- re-plans before the ledger check and refuses if the pending statements changed since preflight;
- after the zero-change ledger check, executes the pinned statement text as one D1 batch, requires every result to
  report success, then requires an empty plan with both objects present and exact, and continues the normal release
  (recovery version, active version, the same final checks);
- does not pause the demo: the version already serving never reads the new table;
- is a no-op once applied: with the exact table and index present the plan is empty and the release is an ordinary
  completed-only release.

The opt-in is refused before any external operation in `maintain` mode, with the one-time OAuth bootstrap, or with any
value other than `true`/`false`. Off (the default), behaviour is unchanged.

**What it executes**, and nothing else:

```sql
CREATE TABLE `external_action_keys` (
	`account_id` text NOT NULL,
	`idempotency_key` text NOT NULL,
	`request_hash` text NOT NULL,
	`action_id` text NOT NULL,
	`created_at` integer NOT NULL,
	PRIMARY KEY(`account_id`, `idempotency_key`),
	FOREIGN KEY (`account_id`) REFERENCES `accounts`(`account_id`) ON UPDATE no action ON DELETE restrict,
	FOREIGN KEY (`action_id`) REFERENCES `external_actions`(`action_id`) ON UPDATE no action ON DELETE restrict,
	CONSTRAINT "external_action_keys_key" CHECK(length(idempotency_key) BETWEEN 1 AND 128 AND idempotency_key NOT GLOB '*[^A-Za-z0-9._:-]*'),
	CONSTRAINT "external_action_keys_hash" CHECK(length(request_hash)=64 AND request_hash NOT GLOB '*[^0-9a-f]*')
);
CREATE UNIQUE INDEX "external_action_keys_action" ON "external_action_keys" ("action_id");
```

**Operator steps (owner only).**

1. Actions → Deploy Weave Worker → Run workflow, from `main`: `configuration_scope: production`,
   `enable_first_party_oauth: false`, `apply_external_action_keys_schema: true`. Approve the protected `production`
   environment when asked.
2. In the deploy step's log, expect `Completed-only approved schema: {"statements":2,...}` followed by the
   `after-ledger-check` validation with `"schemaStatements":0`, then the recovery and active deployments and the public
   MCP check. `test-results/deployment/result.json` records the applied statements in `approvedSchemaApplied`.
3. Later releases go back to the default: the `/weave-deploy-demo` comment or a dispatch with the opt-in off. Running
   it again with the opt-in on is harmless (empty plan).

**Checking it afterwards, read-only.** Post `/weave-d1-inventory` on #236 (or dispatch the read-only D1 inventory
workflow) and look for `external_action_keys` among the tables; or run
`wrangler d1 execute <database> --remote --command "SELECT name,sql FROM sqlite_master WHERE tbl_name='external_action_keys'"`.
The next ordinary completed-only release passing its preflight (`"schemaStatements":0`) is the end-to-end check.

**Failure.** Every refusal before the batch leaves the previous version serving and D1 untouched. The batch is a single
D1 transaction; if a later step fails, the previous version keeps serving with the new (unread) table, and a re-run
continues from an empty plan. Do not drop the table or restore a snapshot.

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
- Not deployable through the current `completed-only` release (see Storage). Envoylet's demo check (propose with a
  key, drop the response, repeat, see one plan on `/account`) waits for that path and an owner deploy.
  (2026-11-12: the path exists; see "Release path" above. The owner deploy is still to run.)
- The release path protects the permitted SQL; it does not lock out a concurrent administrator between its checks.
  Changes they make are caught by the re-plans before the ledger check, after the batch and before activation.
