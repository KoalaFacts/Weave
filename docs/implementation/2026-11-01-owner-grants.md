# Owner page: connected clients and addresses — 2026-11-01

This record covers onboarding slice 5 in `typescript/mailbox-worker` and
`typescript/auth-worker`. A signed-in owner sees, per agent, the MCP clients
connected to it, its keys and its addresses, and can disconnect a client. It is
source and local Worker/D1 verification. **Nothing has been deployed.** The
documented commands and flows were source-checked, not run against a
deployment. It builds on the [MCP record](2026-11-01-mcp-a2a-messages.md) and the
[agent binding record](2026-11-01-agent-binding.md).

## Preserved

- The mailbox `client_grants` mirror stays the enforcement point: every private
  `/mcp` call reads it and refuses a revoked, mismatched or missing grant.
- `HumanDirectory.revokeGrant` is unchanged, and `HumanDirectory` gains no
  method: grants and addresses ride `ownerOverview`, revocation rides
  `ownerAction`.
- Confirm, revoke-key and issue-recovery-code still need a GitHub sign-in from
  the last 10 minutes; reject does not. Every form still carries a signed
  10 minute action token.
- A frozen human sees only the frozen page and cannot act.

## Deliberately changed

- **Client grants carry the client's name.** `client_grants` gains
  `client_name` (at most 128 characters, null when the client gave none or only
  whitespace). It is the name the consent page showed: a CIMD client's
  `client_name` from its metadata document, a DCR client's from its
  registration. The consent nonce Durable Object keeps that name from the page
  to the approval, so approval records it without fetching the document again
  (the SDK's `completeAuthorization` still resolves the document to check the
  redirect URI). It is self-reported: both workers replace control and bidi
  characters with `?`, the page escapes it and labels it "self-reported, not
  verified". The client ID is shown beside it.
- **`ownerOverview` returns, per agent,** up to 50 live grants (newest first),
  up to 20 live addresses (ID, label, public or private, whether it has an agent
  card) and up to 20 grants revoked in the last 30 days. The last list is for
  the authorization server's clean-up only and is never rendered. Address labels
  are agent-supplied and marked unverified on the page.
- **`revoke-grant` is a new owner action** for the same human's grant only
  (another human's grant ID is `not-found`; an already revoked grant is `ok`).
  It is deliberately a protective action and does not need the recent GitHub
  sign-in: the worst a stolen session can do with it is disconnect the owner's
  own clients. Its form member set is exactly `grantId`.
- **The AS records the mailbox `grantId` in the SDK grant's metadata** at
  consent. After the mailbox commit it lists the agent's SDK grants (paged) and
  revokes those whose metadata matches, which also ends their refresh tokens.
- **Effect on the client.** At the resource the revocation applies on the next
  call (the D1 mirror read), which answers 401 `invalid_token`. Once SDK
  clean-up has run, a refresh attempt fails because the SDK grant is gone;
  until then a refresh may still return tokens, and `/mcp` refuses them. An
  agent in that state must not retry but stop and ask its owner to reconnect
  the client through consent. The agent guide says so (guide version 6).

## Removed

- The statements that grant revocation has no owner interface and that SDK
  refresh tokens are not revoked.

## Verification limits

- SDK clean-up is separate from the mailbox commit and can fail or stop at its
  bounds. Whenever it did not finish, the page reports access revoked (it is, at
  the resource) and shows that clean-up will be retried (`cleanup-pending`); an
  SDK failure also logs only the fixed category `grant-sdk-revoke`. This
  includes revoking a grant revoked more than 30 days ago, or one whose
  re-read finds the human frozen. Each load of `/account` retries for grants
  revoked in the last 30 days (at most 20 per agent). Per load it scans at most
  3 agents, taken in turn from a random start, and per agent at most 2 pages of
  100 SDK grants, and it makes at most 20 SDK revocations, which keeps a load
  well under the Workers limit of 1,000 KV operations. One agent's failure does
  not stop the others. If the retry on the visit that shows `cleanup-pending`
  finishes, that page says clean-up is complete instead. After 30 days a
  still-unfinished clean-up is no longer retried; the mailbox check keeps
  refusing the tokens, which expire inside the SDK on their own. The retry is
  idempotent and never un-revokes.
- Nothing records that a grant's SDK clean-up finished, so every load lists the
  SDK grants of up to 3 agents with a revocation in the last 30 days, even after
  their clean-up succeeded. Acceptable at demo scale; it costs KV reads only.
- The retry runs only when the owner loads the page; there is no background job.
- Client names, client IDs and address labels are self-reported and unverified;
  Weave does not confirm that a named client is what it claims.
- Revoking a client has no recent-sign-in check by design. For the other
  actions, GitHub offers no `max_age`, `prompt=login` or `auth_time`, so the
  check is an interactive account selection, not proof that credentials were
  re-entered.
- Verified in local Miniflare/workerd emulation only. No deployment, real OAuth
  issuer, real GitHub consent or live client was exercised.

## Owner app grants (2026-11-17)

Owner decision of 2026-11-17 (item AI-2 of the plan in agent-harness `projects/weave.md`): a person can let one app
manage every agent they own, including agents they own later: create agents, list them, rename them and act as any of
them for addresses, contacts, messages and proposals. Approving an external action stays the person's, on Weave's own
page, and keys and recovery codes stay on `/account`. The owner approved the new table that records these grants and
applying it to production at deploy time.

This is delivered in steps. Step 1 (this section) adds only storage and its release gate; nothing reads or writes the
table yet:

- **Table.** `owner_grants` mirrors one such consent grant per row (grant ID, human, client ID, the client's
  self-reported name, creation and revocation times), with an index on `human_id` and the same checks as
  `client_grants`. It holds no agent ID: the grant covers whatever agents the human owns at the time of each call.
- **Release gate.** Completed-only releases refuse any pending schema statement unless an owner-approved change covers
  it. The `external_action_keys` gate became one instance of `scripts/approved-schema-change.ts`, and
  `scripts/approved-owner-grants.ts` is the second, with its own default-off `apply_owner_grants_schema` input, its
  own workflow gates before release work and inside protected production, and its own deploy variable. With both on, one
  batch applies both; each pending statement must belong to an enabled change, in that change's order. Messages and
  behaviour of the `external_action_keys` opt-in are unchanged.
- **Owner deploy.** Until production has `owner_grants`, every completed-only release must be a manual production
  dispatch with `apply_owner_grants_schema: true` (and `apply_external_action_keys_schema: true` while that table is
  also missing); otherwise it stops before any change, as it does for any missing table.

Verification: `test/approved-owner-grants.test.ts` pins the statements to the generator and covers the combined plan;
`test/deploy.test.ts` runs opted-in completed-only releases on a real Miniflare D1 (owner_grants alone, both changes
in one batch, refusals with either opt-in missing, an unapproved index, ambiguous values, maintain mode and the OAuth
bootstrap); `test/deploy-entry.test.ts` checks the workflow input and gates. Not run against a deployment.

Step 2 makes the mailbox honour owner grants on `/mcp`; the authorization server does not issue them yet (step 3):

- **Recording.** `HumanDirectory.recordOwnerGrant` mirrors a grant for a human who is not frozen, at most 20 live per
  human (`grant-limit`), counted in the same transaction as the insert. `revoke-grant` (owner page) and
  `HumanDirectory.revokeGrant` revoke either kind of grant by its ID. `ownerOverview` lists the live owner grants
  (`ownerGrants`) and those revoked in the last 30 days (`revokedOwnerGrantIds`) for token clean-up.
- **Tokens.** An owner grant's claims carry `owner: true` and the human as both `userId` and `humanId`, never an
  account; an agent's claims stay as they were. Claims that mix the two, or a grant row of the other kind, are `401`.
- **Acting as an agent.** Every agent tool needs the `Weave-Agent` header naming an agent the grant's human owns at that
  moment; a missing, malformed, foreign or departed agent is the tool error `invalid_agent` (HTTP 400 before MCP framing),
  never a different agent. A token for one agent sending the header gets the same error. Each write re-checks, inside its
  own transaction, that the grant is live and the agent is still the human's (`grantCaller` now accepts either kind).
- **Managing agents.** `list_agents` (20 per page), `create_agent` and `rename_agent` need the new scope
  `weave:agents.manage`, are refused to agent tokens whatever scopes they list (`403 insufficient_scope`), take no
  header, and act only on the grant's human: another human's agent is not found. Creating still counts against
  `OWNED_AGENTS_PER_HUMAN` (`agent-limit`) and the hourly registration limits by source address (`capacity`); creating
  and renaming re-check the grant in their own transaction. Names follow the existing visible-name rule.
- **Not recorded.** As with an agent's grant, the mailbox does not log which grant made each call; messages and actions
  record their agent as before.

Verification: `test/owner-grants.test.ts` drives the production `/mcp` composition with Service Binding claims on a real
Miniflare D1 (recording and limits; list, create and rename on the human's agents only; acting as an owned agent, refusals
for a missing, foreign, unknown or departed agent; agent tokens refused management and the header; mixed claims, frozen and
revoked grants; revocation between authorization and each write), and `test/mcp-tool-contract.test.ts` checks the new
tools' results against their published schemas.

Step 3 makes the authorization server issue owner grants and lets the owner see and revoke them:

- **Consent.** A request whose scope includes `weave:agents.manage` gets its own consent page instead of the agent
  picker. It says the app may create, list and rename the person's agents and act as any of them, now or later, and may
  send an action only after the person approved it; and that it cannot approve actions, see or change keys and recovery
  codes, connect platform accounts or act as another person's agent. Its one approve form names `agent=all`. The approval
  is refused (400, nothing recorded) when `agent=all` comes with a request without that scope, or when that scope comes
  with a single-agent choice.
- **Issuing.** On approval the AS mirrors the grant with `HumanDirectory.recordOwnerGrant` before any code exists
  (`grant-limit`, frozen and other failures each answer a page and no code), then completes the SDK authorization with
  the human as the SDK user, the mailbox `grantId` in the grant metadata and props `{owner: true, humanId, grantId}`.
- **Owner page.** `/account` lists these apps under "Apps that manage all your agents" (escaped, self-reported names)
  with **Revoke access**, which needs no fresh sign-in, like an agent's client. Revoking also revokes the SDK grant held
  under the human; each later load retries that clean-up for owner grants revoked in the last 30 days, with the human
  counted as one more entry in the existing per-load limit.
- **Refusal codes.** `rename_agent` on an agent the human does not own is now the tool error `agent-not-found`. A
  problem response whose code had no title wrote `"title":undefined`, which is not JSON, so `/mcp` could not read the
  code: `agent-limit` was affected too. Codes without a title now omit it, and both codes have titles.

Verification: `test/workerd-owner-app-grant.test.ts` runs both Workers in Miniflare with fake GitHub: another person's
agent connected the usual way; both mismatched approvals refused with nothing recorded; consent for all agents; create,
list and rename; acting as the new agent and as one created after consent; refusals without a header, for the other
person's agent, and for a single-agent token naming an agent or managing agents; the account page listing the app; and
revocation ending the next call for every agent while the other person's client keeps working.
`test/workerd-owner-grants.test.ts` revokes an owner grant from `/account` and checks that the SDK grant is gone and the
refresh token gets `invalid_grant`. `test/consent.test.ts` and `test/owner-page.test.ts` check the pages' text, escaping
and forms; the mailbox `test/owner-grants.test.ts` checks the `agent-not-found` and `agent-limit` codes. Each was
checked by removing the guard it covers. Not run against a deployment.

## Push webhooks for apps (2026-11-23)

The owner approved, on 2026-11-23, a signed push webhook for apps (status-change notification 2/2 on the shared plan):
an app that holds an owner app grant registers an `https` callback and is told when an external action of one of that
human's agents changes status, instead of polling. Inbox notices stay. It is delivered in sub-steps W1–W4 (ledger in
[docs/implementation/README.md](README.md#weave-scheduled-task-ledger)).

W1, step 1 (this section) adds only storage and its release gate; nothing reads or writes the table yet:

- **Table.** `app_webhooks`: webhook ID, the owner grant it belongs to (`grant_id`, foreign key to `owner_grants`,
  restrict), the callback `url` (must start with `https://`, at most 2048 characters), `secret_sealed` (the per-webhook
  HMAC secret sealed at rest, 29 to 512 bytes; the app sees the secret once, at registration), and creation and
  revocation times with `revoked_at >= created_at`. Index on `grant_id`. Rows are never deleted, so every registration
  stays recorded. It holds no agent ID: like the grant, a webhook covers whatever agents the human owns at each status
  change.
- **Release gate.** `scripts/approved-app-webhooks.ts` is the third instance of `scripts/approved-schema-change.ts`,
  with its own default-off `apply_app_webhooks_schema` input on the manual production dispatch, its own gates before
  release work and inside protected production, and its own deploy variable (`WEAVE_APPROVED_APP_WEBHOOKS_SCHEMA`).
  An owner comment or a repository-scope release can never opt in.
- **Owner deploy.** Until production has `app_webhooks`, every completed-only release must be a manual production
  dispatch with `apply_app_webhooks_schema: true` (and `apply_owner_grants_schema: true` while `owner_grants` is also
  missing, since a plan holding both refuses with only one opt-in); otherwise it stops before any change. SQLite does
  not need the referenced table to exist when a table is created, so the generator's order (`app_webhooks` first)
  applies cleanly in one batch.

Verification: `test/approved-app-webhooks.test.ts` pins the statements to the generator, refuses a plain-`http`
check, a looser secret length, a unique index, a cascading key and the index before the table, and covers the plan
combined with `owner_grants`; `test/deploy.test.ts` runs opted-in completed-only releases on a real Miniflare D1
(`app_webhooks` alone, with `owner_grants` in one batch, refusals with either opt-in missing, an unapproved index,
ambiguous values, maintain mode and the OAuth bootstrap); `test/deploy-entry.test.ts` checks the workflow input and
gates. Mutations caught: the deploy ignoring the opt-in, the `https` check dropped, an owner comment able to opt in,
and the live-schema read missing from the completed-only allow list. Not run against a deployment.

W1, step 2 adds the registration tools over the private MCP endpoint; still nothing is delivered (W2–W3):

- **Tools.** `register_webhook {url}`, `list_webhooks {}` and `revoke_webhook {webhookId}`, scope `weave:agents.manage`,
  so only an owner grant reaches them (an agent's own token gets `403`), with no `Weave-Agent` header. A webhook belongs to
  the grant that registered it: another grant of the same human neither lists nor revokes it (`webhook-not-found`).
- **Callback.** An absolute `https` URL to a DNS name, of at most 2048 characters, with no user info and no fragment,
  stored normalized (`URL.href`). IP literals (including the numeric forms URL parsing turns into IPv4) and `localhost`
  names are refused. Anything else is the tool error `invalid` on field `url`. Weave does not resolve or call it yet;
  the W3 sender must still check where a name resolves and refuse redirects.
- **Grant end.** Revoking the owner grant or freezing its human leaves the webhook rows unrevoked but unreachable: every
  tool call re-checks the grant. W2–W3 must deliver only while the grant is live and the human not frozen.
- **Secret.** 32 random bytes, base64url (43 characters), returned only in the `register_webhook` result. It is stored
  sealed with AES-256-GCM under `EXTERNAL_CREDENTIAL_KEY`, bound to the webhook and grant IDs, so a sealed secret copied
  to another row fails to open; `list_webhooks` never returns it. Without the key, registration answers `503`.
- **Limits and records.** At most 10 live callbacks per grant (`409 webhook-limit`; revoke one first). Revoking is
  idempotent and sets `revoked_at`; rows are never deleted. The grant is re-checked inside each call's own transaction,
  so a revocation or freeze committed after authorization refuses the write (`grant-revoked`).
- **Owner deploy.** The tools need `app_webhooks` in production (`apply_app_webhooks_schema: true`, step 1); until then
  they fail and write nothing.

Verification: `test/owner-grants.test.ts` (mailbox) covers register, normalized URL, secret shape, sealed bytes that do
not contain the secret and open only with their own grant ID, list without secrets, cross-grant list and revoke refusal,
idempotent revoke, a revocation committed between authorization and the write, refused URLs, the limit and
re-registration after a revoke, and the missing key; refused URLs include `http`, user info, a fragment, IP literals
(`127.0.0.1`, `[::1]`, `0x7f.1`, `2130706433`) and `localhost` names; `test/mcp-tool-contract.test.ts` checks the three
tools' real D1 results against their published output schemas. `test/workerd-owner-app-grant.test.ts` (auth-worker, two workers)
registers, lists, refuses `http`, refuses an agent token, revokes, and checks that the revoked grant cannot touch its
webhooks while every registration stays recorded. Mutations caught: the grant filter dropped from revoke and from list,
the live limit, the `https`, user, password, fragment and IP-literal checks, and the in-transaction grant re-check removed; the
`https` mutation was also caught by the workerd test. Not run against a deployment.

W2, step 1 adds the delivery table and its release gate; nothing writes or sends deliveries yet (W2 step 2, W3):

- **Table.** `app_webhook_deliveries`: one row per status change per registered webhook. `event_id` is the stable event
  ID the receiver deduplicates on; `webhook_id` references `app_webhooks` (`ON DELETE restrict`); `action_id` and
  `status` (checked against the same seven values as `external_actions.status`) are all the body will name, never
  content, credentials or tokens; `occurred_at`; `attempts` (0 to 100); `next_attempt_at`, `NULL` once the delivery is
  finished; `delivered_at`, which requires `next_attempt_at IS NULL` and `delivered_at >= occurred_at`. Index on
  `next_attempt_at` for the sender. `action_id` has no foreign key, so action retention never waits on a delivery;
  pruning finished deliveries is W3's.
- **Release gate.** `scripts/approved-app-webhook-deliveries.ts` is the fourth instance of
  `scripts/approved-schema-change.ts`, with its own default-off `apply_app_webhook_deliveries_schema` input on the manual
  production dispatch, its own gates before release work and inside protected production, and its own deploy variable
  (`WEAVE_APPROVED_APP_WEBHOOK_DELIVERIES_SCHEMA`). An owner comment or a repository-scope release can never opt in.
- **Owner deploy.** Until production has `app_webhook_deliveries`, every completed-only release must be a manual
  production dispatch with `apply_app_webhook_deliveries_schema: true` (with `apply_app_webhooks_schema: true` and
  `apply_owner_grants_schema: true` while those tables are also missing); otherwise it stops before any change.

Verification: `test/approved-app-webhook-deliveries.test.ts` pins the statements to the generator and refuses an extra
status, an unbounded attempt count, a delivered row that is still due, a unique index, a cascading key, a foreign key on
the action and the index before the table, and covers the plan combined with `app_webhooks`; `test/deploy.test.ts` runs
opted-in completed-only releases on a real Miniflare D1 (the table alone, with `app_webhooks` in one batch, refusals with
either opt-in missing, an unapproved index, ambiguous values, maintain mode and the OAuth bootstrap);
`test/deploy-entry.test.ts` checks the workflow input and gates. Mutations caught: the deploy ignoring the opt-in, the
live-schema read missing from the completed-only allow list, the production revalidation step removed, an owner comment
able to opt in, the input defaulting to true, and the attempt bound dropped. Not run against a deployment.

W2, step 2 queues the deliveries; still nothing is sent (W3):

- **When.** Every write that changes an external action's status stages one `INSERT … SELECT` into
  `app_webhook_deliveries` right after the status write, in the same D1 batch: propose (`pending`), the owner's approval
  (`approved`) and rejection or withdrawal (`rejected`), execute's claim (`dispatching`, or `failed` when the plan no longer
  holds) and the provider's outcome (`completed`, `failed` or `unknown`). A plan that lapses with nothing written
  (`expired`) is not a change and queues nothing; its `expiresAt` is on the action read.
- **Who.** One row per live webhook of every live owner grant of the human who owns the action's agent when the change
  is written. A revoked webhook or grant, a frozen human and an ownerless agent get none; revoking later does not remove
  rows already queued (W3 re-checks before sending).
- **Only real changes.** The insert selects the action only if it now has that status with `updated_at` equal to the
  change's own time, so a guarded update that matched nothing (a lost race, a second decision) queues nothing.
- **Event ID.** `<webhookId>:<actionId>:<status>`, stable across retries and unique per receiver; an action reaches each
  status at most once, and a repeated insert is `ON CONFLICT(event_id) DO NOTHING`. `occurred_at` is the change's time,
  `next_attempt_at` equals it (due at once), `attempts` 0. Only the action ID and status are stored for the body.
- **Owner deploy.** Needs `app_webhook_deliveries` in production (step 1's `apply_app_webhook_deliveries_schema: true`);
  without it every status write would fail, which the release gate prevents by refusing a completed-only release until the
  table is applied.

Verification: `test/app-webhook-deliveries.test.ts` (mailbox, real Miniflare D1 through the Worker's HTTP routes and the
owner actions) covers the full lifecycle (`pending`, `approved`, `dispatching`, `completed` with stable event IDs, due at
once, each at its change's time, nothing more on a repeated execute), only live webhooks of live grants of the owner
(two webhooks on one grant, a second grant, a revoked webhook, a webhook of a revoked grant, another human's grant),
revocation midway, rejection and a refused second decision, provider rejection and unknown outcome, a pre-dispatch refusal,
a frozen owner, five concurrent executes (each change queued once), a staged change that did not happen, and no webhooks.
Mutations caught: each of the seven staging calls removed, the frozen, grant-revoked, webhook-revoked, owner, status and
change-time conditions dropped, `next_attempt_at` left `NULL`, and the conflict clause removed. Not run against a deployment.
