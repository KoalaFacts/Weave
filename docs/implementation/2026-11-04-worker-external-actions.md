# Worker governed external actions: owner-approved plan, at-most-once dispatch

## Scope

The first governed external effect in the TypeScript Worker
(`typescript/mailbox-worker`). An owned agent proposes one action through one of its
credentials, the agent's human owner approves that exact plan, and the agent then
executes it once. It follows the existing binding-request pattern (agent request,
owner decision through the `HumanDirectory` RPC with a digest of what the owner was
shown) and carries over the [#276](https://github.com/KoalaFacts/Weave/issues/276)
acceptance list: no provider call before approval, exactly one after, and every
meaningful change after approval refuses dispatch.

Not in this increment: a real provider (LinkedIn, YouTube), a credential connect
flow or encrypted credential storage, approval notifications (#277) and a logical-effect lock across actions (#278).
No remote migration or deployment was run.

## Storage

Two additive tables in `src/schema.ts`:

- `external_credentials` (`account_id`, `credential_ref`) → provider, `revision`,
  provider-verified `target_account` and `target_display`, `revoked_at`. The future
  connect flow writes a new revision on every rotation, re-authorization or
  replacement. This increment has no API that writes it; tests insert rows directly.
  It holds no secret yet.
- `external_actions`: the frozen plan (provider, `contract_revision`, operation,
  content, credential reference and revision, target account), its `plan_digest`,
  status, decision (`decided_at`, `decided_by`) and dispatch record.

## Flow and guarantees

| Step | Where | Effect |
| --- | --- | --- |
| `POST /v1/external-actions` `{credentialRef, operation, content}` | agent | `pending`; provider not called |
| `ownerOverview` → `externalActions[]` | owner RPC | full content, provider, target display and ID, `credentialRef@revision`, `planDigest` |
| `ownerAction` `confirm-action {requestId, planDigest}` / `reject-action` | owner RPC | `approved` or `rejected`; confirm needs fresh authentication |
| `/account` "Approve and allow sending once" / "Reject" | auth worker owner page | signed form token bound to the shown `planDigest`; stale sign-in re-authenticates first |
| `POST /v1/external-actions/{id}/execute` | agent | re-check, commit `dispatching`, call provider once, record outcome |

- The plan digest is SHA-256 over the action and account IDs, provider and its
  contract revision, operation, full content, credential reference and revision,
  target account and expiry. A confirmation with another digest, or after the
  credential's revision or target changed, is `plan-changed`.
- Only an agent with a human owner can propose (`409 unowned-agent`). Another
  human's or agent's action is indistinguishable from a missing one.
- Execute re-checks in one transaction: the agent's key, that the owner is still the
  approving human and not frozen, the credential's revision, target and provider,
  and the provider contract and digest. A failed check records `failed` with the
  reason and `dispatchedAt` null; nothing is sent.
- `dispatching` is committed before the provider call, so concurrent executes send
  once. A provider exception, a non-2xx/422 response or a malformed receipt records
  `unknown`; if the outcome cannot be recorded the row stays `dispatching`. Neither
  is ever dispatched again. A 422 is the only confirmed rejection (`failed`).
- The approval adds no authority; the plan expires one hour after creation, for
  approval and execution together.

## MCP tools

`propose_external_action` and `execute_external_action` (scope `weave:actions.write`)
and `get_external_action` (`weave:actions.read`) call the same functions. For MCP
the caller's live authority is its OAuth consent grant, re-checked inside every
transaction (the HTTP API re-checks the DPoP key instead), so a grant revoked after
approval cannot dispatch. `execute_external_action` is marked open-world. Both
scopes are added to the authorization server and resource metadata;
`test/external-actions-mcp.test.ts` covers scopes, the full MCP flow, a grant
revoked after approval and argument validation. Listing credentials and uploading
media through MCP are in [agent credentials and media upload](2026-11-04-agent-credentials-media-upload.md).

## Provider

`src/external-providers.ts` defines the provider contract. The only provider,
`test-http`, exists only when `EXTERNAL_ACTION_TEST_ENDPOINT` is set; no deployed
configuration sets it. It posts the call to that endpoint and is driven in tests by
Miniflare's `outboundService`.

## Verification

`test/external-actions.test.ts` (30 tests) runs against the built Worker and local
D1: exactly-once after approval with an idempotent re-execute; unowned agent;
missing provider; invalid input including a caller-supplied account; wrong digest
and credential rotation before confirmation; stale owner authentication; another
human's agent; rotation, revocation and retargeting after approval; frozen owner;
expiry; the stored content, operation, contract revision or expiry edited after
approval (`plan-changed`, nothing sent); rejection; 502, malformed receipt and a
lost response recording `unknown` with no resend; confirmed provider rejection; five
concurrent executes sending once; another agent reading or executing. Removing the
revision check, or letting `unknown` be re-dispatched, fails four of these tests.
Removing the digest re-check at execute fails the content, operation and expiry
edits; the contract-revision edit fails only when both the contract-revision check
and the digest re-check are removed, since the digest also covers the revision. The
OpenAPI route test covers the three new paths.
Added later (2026-11-12): the owner changing after approval (the agent moved to
another human, or left unowned) fails with `owner-changed` and nothing sent, on the
first and a repeated execute. No product path moves a bound agent today, so this pins
the dispatch re-check itself. Dropping the approver comparison fails both cases;
refusing only an unowned agent fails the other-human case.
Added later (2026-11-12): the owner's decision itself. Confirming a pending plan whose
credential was revoked or retargeted after it was shown is `plan-changed` and leaves it
pending (only the revision change was covered); confirming past expiry is `expired`
while rejecting still decides it; confirming or rejecting a plan already decided is
`not-pending` and keeps its outcome. Each of the five guards (revoked, target, expiry,
confirm status, reject status) survived every earlier test; removing any one now fails
its new test.

In the auth worker, `test/owner-page.test.ts` checks the action section (escaped
content in `<pre>`, verified account, credential revision, bound form tokens) and
`test/workerd-owner-page.test.ts` runs both Workers together: an owner approves on
`/account` and the agent's execute sends exactly once; a stale sign-in is sent to
GitHub with no effect; a credential rotated after the page was rendered is
`plan-changed`.

## Limits

- Credentials and their revisions are trusted rows with no writer API yet.
- The Worker is the trusted execution path; a real provider will receive a
  decrypted credential there.
- No notification tells the owner an action is waiting (#277); they must open `/account`.
- An agent can have at most 20 actions `pending` or `approved` and unexpired (`429 capacity`,
  no `Retry-After`, because a plan can leave at any time). An approved plan counts until it is
  executed or expires; an expired one frees its place before the cleanup sweep. Pinned by
  `proposeExternalAction_TwentyWaiting_429UntilOneIsExecutedRejectedOrExpired` (2026-11-11);
  mutations killed: `approved` not counted, expiry ignored, `>` for `>=`, the count across agents.
  Field and content checks run first: a full agent's plan with an unknown `credentialRef` or
  blank content gets `400 invalid`, not `429`, so the caller fixes it rather than waiting
  (mutation killed: the count moved before the field checks).
