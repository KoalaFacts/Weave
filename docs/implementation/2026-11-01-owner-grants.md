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
Mutations caught: each of the six staging calls removed, the frozen, grant-revoked, webhook-revoked, owner, status and
change-time conditions dropped, `next_attempt_at` left `NULL`, and the conflict clause removed. Not run against a deployment.

W3 sends the queued deliveries (`src/webhook-sender.ts`, run by the mailbox Worker's five-minute cron after the other
sweeps):

- **Claim before send.** One transaction reads up to 15 due rows (`next_attempt_at` reached) and, for each, counts the
  attempt and schedules its retry before anything is sent: a run that dies mid-send still retries, and the optimistic
  mailbox revision means two runs never claim the same attempt. A second transaction marks the accepted ones
  (`delivered_at`, `next_attempt_at = NULL`). Each phase has its own 48-statement budget (21 with W4's pruning delete, and 19 statements per try);
  one retried twice after concurrent writes is refused: a refused claim sends nothing, refused marking leaves the sent
  deliveries due, so they are sent again.
- **Who still gets it.** The claim re-checks that the webhook and its grant are unrevoked, the human not frozen and the
  action's agent still owned by the grant's human. Otherwise the delivery is finished unsent (`next_attempt_at = NULL`,
  no attempt counted), so a revocation stops queued deliveries too.
- **Request.** `POST` to the registered URL with `Content-Type: application/json`, `User-Agent: Weave-Webhooks/1`,
  `Weave-Event-Id`, `Weave-Webhook-Id` and `Weave-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "<t>.<body>">` under
  the webhook's secret (opened from its sealed copy, bound to the webhook and grant IDs). The body is exactly
  `{eventId, type: "external_action.status", webhookId, actionId, status, occurredAt}`, never content, credentials or
  tokens. At most 6 requests are in flight at once (a Worker's connection limit; each timeout starts only when its request
  does). `redirect: 'manual'`, a 5-second timeout, and only a `2xx` counts; a redirect, other status, timeout or network
  error is a failed attempt. The response body is discarded unread.
- **Retries.** At most 10 attempts; the retry delay starts at one minute and doubles, the last one 256 minutes (about 8.5
  hours in all; the cron's five-minute period rounds the first ones up). After the tenth failure the delivery is given up
  (`next_attempt_at = NULL`, `delivered_at` stays `NULL`). Order between one action's events is not guaranteed; the
  receiver deduplicates on `eventId` and orders by `occurredAt`.
- **Never the action.** The sender reads the action only to check ownership and writes nothing but delivery rows, so a
  delivery failure never changes the action.
- **Owner deploy.** None beyond W2's `app_webhook_deliveries` (`apply_app_webhook_deliveries_schema: true`). Without
  `EXTERNAL_CREDENTIAL_KEY`, or in maintenance or recovery read-only mode, nothing is sent and rows stay due.

Verification: `test/app-webhook-sender.test.ts` (mailbox, real Miniflare D1 through the Worker's own scheduled handler
and a fake receiver on the outbound service) covers the signed body checked against an independent HMAC, one delivery
per event, a later change with its own event ID, a `503` retried a minute later with the same event ID and the action
unchanged, a redirect not followed, the give-up after the tenth attempt, a receiver that never answers abandoned at the
timeout, a revoked webhook, a revoked grant, a frozen human and an agent now owned by someone else finished unsent, 16 due
deliveries sent as 15 then 1 within the budget, never more than 6 requests in flight, and nothing sent without the key. `test/workerd-owner-app-grant.test.ts`
(auth-worker, two workers) proposes as an owned agent through the owner app's grant and checks that the mailbox cron
delivers the event only to the live webhook, signed with the secret `register_webhook` returned. Mutations caught:
`redirect: 'manual'` removed, the timeout removed, the whole liveness check, the frozen-human and ownership checks each
dropped, any status counted as accepted, the give-up bound removed, the retry scheduled immediately, 16 per run, 15 requests in flight, the
signature over the body alone and under the wrong key, and (workerd) the sender left out of the cron. The revoked-webhook
mutation is not caught by the workerd test, because W2 queues nothing for a revoked webhook; the mailbox test catches it.
The sender does not resolve the callback's name itself: it relies on registration refusing IP literals and `localhost`
and on a Worker's outbound `fetch` leaving through
Cloudflare's public network (the mailbox Worker does not set `global_fetch_strictly_public`); pruning finished deliveries is left to W4. Not run
against a deployment.

W4, step 1 prunes finished deliveries (`src/webhook-sender.ts`, in the sender's claim transaction, so each cron run that
sends also prunes). A delivery with `next_attempt_at = NULL` (delivered, given up, or finished unsent) is deleted once its
status change (`occurred_at`) is more than 7 days old, at most 64 a run, oldest first. A delivery still due is never
deleted: every one finishes within about 8.5 hours of its change. The registrations in `app_webhooks` are kept, revoked
ones included, as the record of who registered what. A pruned event is not queued again: its ID is unique per webhook,
action and status, and an action reaches each status once. No schema change and no owner deploy beyond W3's. Like
sending, pruning does not run without `EXTERNAL_CREDENTIAL_KEY` or in maintenance or recovery read-only mode.

Verification: `test/app-webhook-sender.test.ts` (mailbox, real Miniflare D1 through the scheduled handler) ages a delivered,
a given-up, a recent and a still-due delivery and checks that only the first two are deleted, with the action and the four
registrations unchanged, and that 69 old finished rows are pruned as 64 then 5. Mutations caught: the delete removed, the
`next_attempt_at IS NULL` guard dropped, the retention period set to zero, and the per-run bound removed. Not run against
a deployment.

W4, step 2 shows the registrations on the owner page. `HumanDirectory.ownerOverview` returns each live owner grant with
`webhooks`, its live callbacks (`webhookId`, `url`, `createdAt`), oldest first, read by grant ID in the overview's own
transaction (at most 20 grants of 10 callbacks each; never a secret). The read is one more statement per attempt (17 of
the 48-statement budget): an overview retried twice after concurrent writes is now refused with `503` instead of fitting
exactly, which fails closed. Under each app in "Apps that manage all your agents"
on `/account`, the auth Worker lists the URLs as escaped text, never links ("Weave sends it the status of your agents'
actions (never their content) at: …"), or says "It receives no status updates from Weave."; revoking the app's access stops
them all. A mailbox Worker deployed before this field (no `webhooks` member) shows the app without either sentence, so the
auth Worker may be deployed first. Agent Start is unchanged: it is written for agents, and webhooks belong to owner apps,
whose MCP tool descriptions already state the delivery contract. No schema change; an owner deploy of both Workers
(`completed-only`).

Verification: `test/owner-grants.test.ts` (mailbox, Miniflare D1) registers three callbacks on one grant and revokes one,
and checks the overview lists the two live ones on that grant only, nothing for a grant without callbacks or a revoked
grant, and only their own for another human. `test/owner-page.test.ts` (auth) checks the hostile URL is escaped and never a
link, the empty sentence, and the older-mailbox case. `test/workerd-owner-app-grant.test.ts` (two Workers) checks `/account`
lists the live callback registered through `register_webhook` and not the revoked one. Mutations caught: the revoked
condition dropped, callbacks not filtered by grant, the URL unescaped, the list not rendered, and the missing member not
tolerated. Not run against a deployment.

## Apps that decide for the owner (2026-11-23)

A1, approved by the owner on 2026-11-23: an owner may let the app holding one owner grant approve or reject that
owner's agents' pending plans. It is off by default, chosen per OAuth client on Weave's own consent page and revocable
on `/account`; the constraints are in [advance-weave](../../.claude/skills/advance-weave/SKILL.md#project-rules).
Step 1 of A1-1 adds the table and its release gate; step 2 lets the owner choose it on the consent page and stop it on
`/account`. A1-2 records which grant decided (step 1) and lets the app decide (step 2); A1-3 shows that grant on the
owner page.

- **Table.** `owner_grant_approvals`: one row per owner grant whose owner chose to let the app decide. `grant_id` is
  the primary key and references `owner_grants` (`ON DELETE restrict`); `human_id` (required, references `humans`,
  `ON DELETE restrict`) repeats the grant's human so a decision can check both in one lookup; `created_at`;
  `revoked_at`, never before `created_at`. Index on `human_id` for `/account`. A grant without a row can never decide,
  so the permission is off by default; a row has no default, so it exists only when the owner chose it. Rows are never
  deleted: `revoked_at` records the end, so every decision keeps the permission that allowed it.
- **Release gate.** `scripts/approved-owner-grant-approvals.ts` is the fifth instance of
  `scripts/approved-schema-change.ts`, with its own default-off `apply_owner_grant_approvals_schema` input on the
  manual production dispatch, its own gates before release work and inside protected production, and its own deploy
  variable (`WEAVE_APPROVED_OWNER_GRANT_APPROVALS_SCHEMA`). An owner comment or a repository-scope release can never
  opt in.
- **Owner deploy.** Until production has `owner_grant_approvals`, every completed-only release must be a manual
  production dispatch with `apply_owner_grant_approvals_schema: true` (with `apply_owner_grants_schema: true` while
  `owner_grants` is also missing); otherwise it stops before any change.

- **Choosing it (A1-1 step 2).** The owner consent page carries one unticked box inside the approve form, "Also let
  this app approve or reject the pending external actions of all your agents for you", and its list of what the app
  cannot do now says so unless the box is ticked. `readConsent` accepts `appDecides` only once, only as `yes`, and only
  on an approval for all agents, so an agent's own grant can never carry it. `recordOwnerGrant` takes `appDecides`
  (boolean, absent means false) and inserts the `owner_grant_approvals` row in the same transaction as the grant, so a
  refused grant (frozen human, grant limit) records no permission either.
- **Seeing and stopping it.** `ownerOverview` gives each live owner grant `appDecides`, true while its row is not
  revoked. `/account` says per app whether it may approve or reject, and offers "Stop it approving" (owner action
  `stop-app-decisions`) for one that may. Stopping is protective like revoking, so it needs no fresh sign-in; it sets
  `revoked_at` once (idempotent), keeps the grant's other access, and answers not-found for another human's grant or a
  grant without the permission. Revoking the owner grant sets the row's `revoked_at` in the same transaction. A mailbox
  that predates the field shows no sentence.
- **Limits.** An auth-worker deployed ahead of the mailbox sends `appDecides` to a mailbox that ignores it: the grant
  is recorded without the permission (fails safe; the release gate already requires the table). Stopping does not
  re-check that the grant is live, so A1-2's decision must itself refuse revoked grants and frozen humans, not rely on
  the row alone.

- **Recording the deciding grant (A1-2 step 1).** `owner_grant_decisions`: one row per app decision, written (in
  step 2) in the same transaction as the decision on `external_actions`, whose `decided_by` stays the human the grant
  acts for. Primary key (`action_id`, `decision`): an action has at most one approval and one rejection, because the
  owner (or the app) may still withdraw an approval that was never used. `decision` is `approved` or `rejected`;
  `grant_id` (required, references `owner_grants`) and `action_id` (references `external_actions`) are both
  `ON DELETE restrict`; `decided_at` has no default. Index on `grant_id`. A decision on Weave's own owner page writes no
  row, so a decision without a row was the owner's own. Rows are never deleted. Release gate:
  `scripts/approved-owner-grant-decisions.ts`, the sixth instance of `scripts/approved-schema-change.ts`, with its own
  default-off `apply_owner_grant_decisions_schema` input, gated before release work and again inside protected
  production, and its own deploy variable (`WEAVE_APPROVED_OWNER_GRANT_DECISIONS_SCHEMA`). Until production has the
  table, every completed-only release must be a manual production dispatch with `apply_owner_grant_decisions_schema:
  true` (with `apply_owner_grants_schema: true` while `owner_grants` is also missing); otherwise it stops before any
  change. Step 2 writes it.

- **Deciding (A1-2 step 2).** Three owner-grant MCP tools (scope `weave:agents.manage`, no `Weave-Agent` header):
  `get_pending_action {actionId}` returns one pending, unexpired plan of the human's agents exactly as the owner page's
  `pendingForOwner` builds it, including `planDigest` and `accountChanged` (`action-not-found` for anything else; it needs
  no permission to decide, because the grant can already act as those agents). `approve_action {actionId, planDigest}`
  and `reject_action {actionId}` (`src/app-decisions.ts`) run, in one transaction: the grant check (live, this human's,
  human not frozen; otherwise 401 `grant-revoked`), the grant's live `owner_grant_approvals` row (otherwise 403
  `decisions-off`), then the owner page's own `confirmExternalAction` / `rejectExternalAction` with the human as
  `decided_by`, and on success the `owner_grant_decisions` row. So the exact digest, revalidation of the provider
  contract and credential (`plan-changed`, `account-changed`), expiry (`expired`), `not-pending` once decided or sent,
  withdrawal of an unused approval, and `dispatching` / `unknown` never reopened are unchanged, and approval still sends
  nothing: the agent executes. Unlike the owner page there is no fresh-sign-in window: the owner's consent-page choice is
  the authority, and stopping it or revoking the grant ends it on the next call. An agent's own grant never reaches these
  tools (403 at the scope check). The MCP layer names each refusal code with a hint.

- **Showing which app decided (A1-3).** `ownerPendingAction`'s `settled` view carries `decidedByApp` (`grantId`,
  `clientId`, `clientName`, or null): the grant recorded in `owner_grant_decisions` for the decision the action's status
  rests on, the rejection for a rejected action and the approval for any other decided one (approved, sending, sent,
  failed, `unknown`, or approved and expired unsent). The owner withdrawing an app's approval on their own page leaves it
  null, and an app withdrawing the owner's approval names the app, because the owner page writes no row. A plan that
  expired undecided is null. The page for one action (`/account/actions/<id>`) then says "An app made this decision for
  you", with the client's self-reported name (escaped, marked not verified, "Unnamed client" when absent) and client ID,
  and an approved action reads "An app you let decide for you approved this action" instead of "You approved this
  action". A mailbox that predates the field, or a malformed value, adds nothing. No schema, API or MCP change.
- **In the recent-actions list (2026-11-24).** Each `/account` "Recent actions" entry (`recentForOwner`) carries the
  same `decidedByApp`, from the same `owner_grant_decisions` row joined in the list's one query, and an entry an app
  decided adds "decided by an app you let decide:" with the client's self-reported name (escaped, marked not verified,
  "Unnamed client" when absent or empty) and client ID. The owner's own decisions, a plan expired undecided, a mailbox
  that predates the field and a malformed value add nothing. No schema, API or MCP change.
- **Listing what waits (2026-11-24).** An app that decides learned of a pending plan only from a webhook or by listing
  each agent's plans as that agent, one agent at a time. The owner-grant MCP tool `list_pending_actions {}` (same
  scope, no `Weave-Agent` header, no permission to decide needed, like `get_pending_action`) returns
  `{items, more}`: the 20 oldest pending, unexpired plans across all the human's agents, each exactly as
  `get_pending_action` returns it (the owner page's `pendingForOwner`, in its order), and `more` true when others wait
  behind them; deciding these, or letting them expire, brings the next forward. A revoked grant or frozen human is 401
  `grant-revoked`; it takes no arguments. No schema or HTTP API change.

Verification of A1-3: `test/owner-grant-decisions.test.ts` covers an app approval (before and after the agent sends
it), an app rejection, the owner withdrawing an app's approval (null), the app withdrawing the owner's approval (the
app), an owner rejection and a plan expired undecided (null), and an app approval that expired unsent (the app);
auth-worker `test/owner-page.test.ts` checks the wording for every settled status, escaping of name and client ID,
an unnamed client, and the owner's wording kept for null, absent and malformed values; `test/workerd-owner-app-grant.test.ts`
opens the owner's page for the action the app approved and the agent sent, across both Workers, and finds the app named.
Mutations caught: the decision looked up fixed to `approved`, the lookup not limited to its action, the approved wording
not switched, the client name unescaped, and the client-ID check removed. The `decided_at` null short-cut is an
equivalent mutant (an undecided action has no decision row); it only saves a read. Not run against a deployment; it
reads the A1-2 table, so production needs both A1 opt-ins.

Verification of the recent-actions list: `test/owner-grant-decisions.test.ts` covers, through `ownerOverview`, an app
approval that was sent, an app rejection, the owner withdrawing an app's approval and an owner approval (null), the app
withdrawing the owner's approval (the app), each action listed once, another human's app decision absent from this list
and named on that human's own, an app approval that expired unsent (the app) beside a plan expired undecided (null), and 25
app-decided actions, half with both an approval and a rejection row, paged once each across two pages;
auth-worker `test/owner-page.test.ts` checks the entry's wording, escaping of name and client ID, a null or empty name,
and nothing added for null, absent and malformed values; `test/workerd-owner-app-grant.test.ts` reads `/account` across
both Workers and finds the app named on the entry of the action the app approved and the agent sent. Mutations caught:
the decision joined fixed to `approved`, the join not limited to its decision or to its action, the field always null, the client name
dropped (mailbox); the name unescaped, the client ID unescaped, the client-ID check removed, the entry not showing the
app (also caught by the workerd test), and an empty name shown as a name (auth-worker). The `decided_at` condition in
the join is equivalent, as above.

Verification of the pending list: `test/owner-grant-decisions.test.ts` covers an empty list, plans of two agents of
the human oldest first with another human's and an expired plan left out, each item equal to `get_pending_action`'s
view, 21 waiting (the oldest 20, `more` true), one decided (the next brought forward, `more` false), an argument
refused (400) and a revoked grant (401); `test/mcp-tool-contract.test.ts` checks the result against its output schema;
auth-worker `test/workerd-owner-app-grant.test.ts` lists the queued plan through both Workers. Mutations caught: the
look-ahead row dropped (`more` always false), the page not cut at 20, the grant check removed, and the argument check
removed. Not run against a deployment.

Verification of A1-2 step 2: `test/owner-grant-decisions.test.ts` covers approve as the human with the grant recorded and
one send, no permission and a stopped permission (`decisions-off`, nothing written), a revoked grant and a frozen human
(401), another human's action (404, and naming the other human with this grant is 401), a wrong digest and a changed
credential, expiry (approve refused, reject allowed), withdrawal of an unused approval but never of a sent or `unknown`
action, an owner-page decision writing no row, malformed arguments and maintenance mode, and `get_pending_action`
(the owner page's digest, another human's and a decided plan not found, a revoked grant 401);
`test/mcp-tool-contract.test.ts` checks the three results against their output schemas; auth-worker
`test/workerd-owner-app-grant.test.ts` drives both Workers: the deciding app reads the plan, a wrong digest is
`plan-changed`, an agent token is 403, the approval records the grant with the human as `decided_by`, the agent
executes once against the fake provider, and after "Stop it approving" on `/account` both tools answer `decisions-off`.
Mutations caught: the permission check removed, its `revoked_at` ignored, the grant check removed (both in the decision
and in the read), the decision row not written, the decision label fixed to `approved`, the digest format check
removed, and (at the two-Worker boundary) the permission check removed. A `human_id` condition on the permission read
was dropped because the grant check already binds the human and no test could tell it apart. Not run against a
deployment; production needs both A1 opt-ins (`apply_owner_grant_approvals_schema`, `apply_owner_grant_decisions_schema`).

Verification of A1-2 step 1: `test/approved-owner-grant-decisions.test.ts` pins the statements to the generator and
refuses a default on `decided_at`, a nullable grant, a key on the action alone, an unchecked decision value, a unique
index, a cascading or no-action key on either reference and the index before the table, and covers the plan combined
with `owner_grants`; `test/schema.test.ts` checks the constraints at insert time; `test/deploy.test.ts` runs opted-in
completed-only releases on a real Miniflare D1 (the table alone, all six approved tables in one batch, refusals with the
opt-in missing or `owner_grants` also missing, an unapproved index, ambiguous values, maintain mode and the OAuth
bootstrap); `test/deploy-entry.test.ts` checks the workflow input and that both gate steps exist. Mutations caught: the
deploy ignoring the opt-in, the live-schema read missing from the completed-only allow list, the input defaulting to
true, an owner comment able to opt in, either gate step removed, the decision check dropped, and (independent
reviewer) `restrict` weakened to `no action`. Not run against a deployment.

Verification of step 2: `test/owner-grants.test.ts` (`recordOwnerGrant_AppDecides_RecordedOnlyWhenChosenShownAndStoppable`)
covers off-by-default, a non-boolean refused, the overview flag, stop (old sign-in, idempotent, first time kept, grant
still live), another human and a grant without the permission refused, revocation ending it, and a frozen human;
auth-worker `test/consent.test.ts` covers the box and every refused combination; `test/owner-page.test.ts` the sentences
and the stop form's token; `test/workerd-owner-app-grant.test.ts` drives both Workers: an agent consent with the box is
refused, an owner consent with it records one row (and the earlier owner grant none), `/account` shows it, the stop form
ends it with result `done` while the app's `list_agents` still works, and the page then says it cannot decide. Mutations
caught: the box accepted on any approval, its value unchecked, the row inserted unconditionally, the stop ignoring the
human, revocation keeping the permission, the overview ignoring `revoked_at`, the consent always passing true, and the
stop token never issued. Not run against a deployment; the opt-in `apply_owner_grant_approvals_schema` of step 1 still
applies.

Verification of step 1: `test/approved-owner-grant-approvals.test.ts` pins the statements to the generator and refuses a default
on `created_at`, a revocation check against a constant, a missing `human_id`, a unique index, a cascading key on either
reference and the index before the table, and covers the plan combined with `owner_grants`; `test/deploy.test.ts` runs
opted-in completed-only releases on a real Miniflare D1 (the table alone, with the owner grant and webhook tables in one
batch, refusals with either opt-in missing, an unapproved index, ambiguous values, maintain mode and the OAuth
bootstrap); `test/deploy-entry.test.ts` checks the workflow input and gates. Mutations caught: the deploy ignoring the
opt-in, the live-schema read missing from the completed-only allow list, the input defaulting to true, an owner comment
able to opt in, and the revocation check dropped. Not run against a deployment.
