# Implementation records

Each record states what one change delivered, how it was verified and what it does not do. Records are history:
they describe the code when they were written and are not rewritten later, so a newer record or the code itself
wins where they differ. A record superseded by a later one says so in its first lines.

Current rules live in [AGENTS.md](../../AGENTS.md) and target semantics in [ARCHITECTURE.md](../../ARCHITECTURE.md).
Newest first within each area. Every record (or dated record section) in this directory is listed once.

## Publishing router and governed external actions (TypeScript Workers)

- 2026-11-06: [LinkedIn publishing adapter](2026-11-05-publishing-router.md#native-linkedin-adapter-2026-11-06)
- 2026-11-06: [X publishing adapter](2026-11-05-publishing-router.md#native-x-adapter-2026-11-06)
- 2026-11-06: [External-action status notices on the inbox](2026-11-06-external-action-notices.md)
- 2026-11-05: [Retrying a proposal safely: `Idempotency-Key` on external actions](2026-11-05-propose-idempotency.md)
- 2026-11-05: [Telegram publishing adapter](2026-11-05-telegram-adapter.md)
- 2026-11-05: [Publishing router: one post format, one connect flow, platforms as adapters](2026-11-05-publishing-router.md)
- 2026-11-05: [Discord publishing adapter](2026-11-05-discord-adapter.md)
- 2026-11-05: [Bluesky: the first native publishing adapter](2026-11-05-bluesky-adapter.md)
- 2026-11-05: [A page for one action: app-linked approval (A2)](2026-11-05-action-approval-page.md)
- 2026-11-04: [YouTube: owner-connected credentials and governed uploads](2026-11-04-youtube-credentials.md)
- 2026-11-04: [Worker governed external actions: owner-approved plan, at-most-once dispatch](2026-11-04-worker-external-actions.md)
- 2026-11-04: [Postiz publishing: text, images and video through owner-approved Postiz channels](2026-11-04-postiz-publishing.md)
- 2026-11-04: [Agent credential listing, MCP media upload and URL import](2026-11-04-agent-credentials-media-upload.md)

## Governed Tools: invocations, approvals and authority (.NET)

- 2026-10-29: [Quorum (multi-person) approval](2026-10-29-quorum-approval.md)
- 2026-10-29: [Human-only approval decisions](2026-10-29-human-approval-principals.md)
- 2026-10-01: [Local Codex first use through the published CLI](2026-10-01-local-governed-first-use.md)
- 2026-09-30: [HTTP cancellation entry diagnostics](2026-09-30-cancellation-entry-diagnostics.md)
- 2026-09-28: [Outbound MCP HTTP and stdio: 2026-07-28 and 2025-11-25](2026-09-28-mcp-modern-http.md)
- 2026-09-24: [Authorized UUID proposal storage, review and continuation](2026-09-24-authorized-proposal-uuid.md)
- 2026-09-23: [Codex + human-controlled write pilot](2026-09-23-codex-human-pilot.md)
- 2026-09-22: [Trusted operator onboarding on one Host](2026-09-22-trusted-operator-onboarding.md)
- 2026-09-22: [Echo HTTP connection-close lifecycle (#92)](2026-09-22-echo-http-close-lifecycle.md)
- 2026-09-21: [MCP HTTP response and lifecycle boundaries](2026-09-21-mcp-http-boundaries.md)
- 2026-09-20: [Verified readable approval review](2026-09-20-verified-approval-review.md)
- 2026-09-20: [Reviewed approval decisions and operator example](2026-09-20-reviewed-approval-decisions.md)
- 2026-09-20: [Read-only operator review screen](2026-09-20-operator-review-screen.md)
- 2026-09-20: [HTTP ingress merge review corrections](2026-09-20-http-merge-review.md)
- 2026-09-20: [Governed invocation HTTP entry](2026-09-20-governed-http-entry.md)
- 2026-09-20: [Fail-closed capability revocation](2026-09-20-fail-closed-revocation.md)
- 2026-09-20: [M1 increment: durable approval before execution](2026-09-20-durable-approval.md)
- 2026-09-20: [Direct HTTP connection isolation](2026-09-20-direct-http-connection-isolation.md)
- 2026-09-20: [Agent-only HTTP surface](2026-09-20-agent-only-http-surface.md)
- 2026-09-19: [Invocation leak surfaces](2026-09-19-invocation-leak-surfaces.md)
- 2026-09-19: [M1 prerequisite: tool request boundary](2026-09-19-invocation-boundary.md)
- 2026-09-19: [M1 increment: explicit tool operations](2026-09-19-exact-tool-operations.md)
- 2026-09-19: [Durable tool invocations](2026-09-19-durable-invocations.md)
- 2026-09-19: [Audit subscriber lifecycle](2026-09-19-audit-subscriber-lifecycle.md)

## Repository, tooling and release chain

- 2026-10-20: [GitHub plugin marketplace — setup preview](2026-10-20-github-plugin-marketplace.md)
- 2026-10-14: [Repository Node sources on native TypeScript](2026-10-14-worker-typescript-tools.md)
- 2026-09-27: [Dependency license policy after the deny-list deprecation](2026-09-27-license-policy-migration.md)
- 2026-09-22: [License evidence and fork-permission acceptance (#110)](2026-09-22-license-fork-acceptance.md)
- 2026-09-22: [Pending dependency PR integration](2026-09-22-dependency-pr-integration.md)
- 2026-09-21: [Retained branch audit and registry integration](2026-09-21-branch-integration.md)
- 2026-09-20: [Dependency comparison evidence and NuGet snapshot production](2026-09-20-dependency-review-evidence.md)
- 2026-09-19: [Security remediation](2026-09-19-security-remediation.md)
- 2026-09-19: [Release-chain security hardening](2026-09-19-release-chain-security.md)
- 2026-09-19: [Flat Source Foundation](2026-09-19-flat-source-foundation.md)

## Agent network: mailbox host and demo Workers

- 2026-11-05: [Protected first-party Weave OAuth issuer rollout (#236)](2026-11-05-protected-first-party-issuer-rollout.md)
- 2026-11-05: [Completed-only releases no longer pause the demo](2026-11-05-completed-only-without-pause.md)
- 2026-11-05: [One release for both demo Workers](2026-11-05-auth-worker-code-update.md)
- 2026-11-01: [Owner page: connected clients and addresses](2026-11-01-owner-grants.md)
- 2026-11-01: [MCP text messages, revocable grants and Agent Cards](2026-11-01-mcp-a2a-messages.md)
- 2026-11-01: [Client packaging and agent key helpers](2026-11-01-client-packaging.md)
- 2026-11-17: [Owner app grants](2026-11-01-owner-grants.md#owner-app-grants-2026-11-17)
- 2026-11-17: [Owners with many agents](2026-11-01-agent-binding.md#owners-with-many-agents-2026-11-17)
- 2026-11-01: [Agent binding, owned-agent keys and unclaimed limits](2026-11-01-agent-binding.md)
- 2026-10-31: [Agent keys replace the bearer account control](2026-10-31-agent-keys.md)
- 2026-10-30: [Two independent agents over the Access trial](2026-10-30-access-trial-two-agent-acceptance.md)
- 2026-10-29: [Access trial activation and approved identity-link storage](2026-10-29-access-trial-activation.md)
- 2026-10-26: [Owner-approved provider-neutral OAuth subject links](2026-10-26-provider-neutral-oauth-subject-links.md)
- 2026-10-26: [Optional D1 account ownership → independently consented OAuth handoff](2026-10-26-oauth-account-proof.md)
- 2026-10-25: [Remote MCP integration: public discovery first](2026-10-25-remote-mcp-public.md)
- 2026-10-25: [Remote MCP: per-user delegated authorization and private mailbox operations](2026-10-25-remote-mcp-oauth.md)
- 2026-10-22: [Model-driven pair acceptance runner](2026-10-22-model-driven-pair.md)
- 2026-10-14: [Worker account storage and retained contact history](2026-10-14-worker-account-storage.md)
- 2026-10-14: [Anonymous registry browsing in creation order](2026-10-14-registry-created-pagination.md)
- 2026-10-14: [Demo Agent Start and non-secret invitations](2026-10-14-agent-entry.md)
- 2026-10-12: [Native Worker and D1 mailbox demo](2026-10-12-worker-d1-demo.md)
- 2026-10-12: [Demo registry, MCP and cleanup security verification](2026-10-12-demo-security.md)
- 2026-10-07: [Agent contact and mailbox MVP](2026-10-07-agent-contact-mailbox-mvp.md)

## Plugins, workspaces and runtime lifecycle (.NET)

- 2026-10-03: [Command process runtime observation](2026-10-03-process-runtime-observation.md)
- 2026-10-03: [Finite command process lifecycle](2026-10-03-process-lifecycle-hardening.md)
- 2026-10-01: [Workspace readiness includes current service health](2026-10-01-workspace-service-readiness.md)
- 2026-10-01: [External Python TCP client: routing readiness and pipe readers](2026-10-01-python-tcp-client-stability.md)
- 2026-09-30: [Explicit workspace runtime reconciliation](2026-09-30-workspace-runtime-reconciliation.md)
- 2026-09-30: [Workspace runtime readiness](2026-09-30-workspace-runtime-readiness.md)
- 2026-09-30: [Workspace runtime observation and explicit container recovery](2026-09-30-workspace-runtime-observation.md)
- 2026-09-30: [Verify current services before confirming workspace recovery](2026-09-30-workspace-recovery-verification.md)
- 2026-09-30: [Workspace network observation and dependency-aware recovery](2026-09-30-workspace-network-recovery.md)
- 2026-09-30: [Installed MCP tool restoration during workspace reconciliation](2026-09-30-workspace-mcp-restoration.md)
- 2026-09-30: [Current MCP service observation](2026-09-30-workspace-mcp-observation.md)
- 2026-09-30: [Continuous plugin installation readiness diagnostics](2026-09-30-plugin-readiness-probes.md)
- 2026-09-29: [Declared plugin service dependencies](2026-09-29-plugin-service-dependencies.md)
- 2026-09-29: [Plugin installation revision envelope](2026-09-29-plugin-installation-revision.md)
- 2026-09-29: [Management operation admission journal](2026-09-29-management-operation-journal.md)
- 2026-09-29: [Management authority, workspace runtime identity, and audit decision](2026-09-29-management-lifecycle-boundaries.md)
- 2026-09-28: [Workspace installation inventory](2026-09-28-workspace-installation-inventory.md)
- 2026-09-26: [Workspace-scoped HTTP MCP Echo installation](2026-09-26-mcp-echo-installation.md)
- 2026-09-25: [Plugin activation ownership](2026-09-25-plugin-activation-scope.md)
- 2026-09-25: [Dapr tool plugin path](2026-09-25-dapr-tool-plugin-path.md)
- 2026-09-22: [Single-Host retained-storage recovery](2026-09-22-single-host-storage-recovery.md)
- 2026-09-22: [Single-Host lifetime and process recovery](2026-09-22-single-host-lifetime-recovery.md)

## Weave scheduled-task ledger

The scheduled Weave task (publishing router and governed actions, `typescript/mailbox-worker` and
`typescript/auth-worker`) keeps its shared plan, requests and current state on
[issue #365](https://github.com/KoalaFacts/Weave/issues/365), the channel with Envoylet. This section holds its run
log. Records above stay the evidence for what each change delivered.

Last docs housekeeping: 2026-11-24 (Sydney).

### Items

| Item | Status |
| --- | --- |
| `propose-idempotency` (Envoylet request): `Idempotency-Key` on `POST /v1/external-actions` | done, deployed (#421, `66482d4`; #422; release path #423; supersedes #366): the owner's release run 43154439279 at `b0ae6f5` applied the approved table on 2026-11-12 04:24 UTC ([record](2026-11-05-propose-idempotency.md#release-path-applying-the-table-to-production-2026-11-12)) |
| Status-change notification for apps (shared plan step 4) | done (deployed in 0.1.0; opt-ins `apply_owner_grants_schema`, `apply_app_webhooks_schema` and `apply_app_webhook_deliveries_schema`): 1/2 external-action notices on the inbox (#369, `0d2d22e`, deployed 2026-11-05); 2/2 a signed push webhook for owner apps (owner approved 2026-11-23): W1 registration tools and table (#541, #548), W2 delivery rows written with each status change (#550, #551), W3 the signed at-least-once sender (#552), W4 pruning and `/account` listing (#553, #554) ([record](2026-11-01-owner-grants.md#push-webhooks-for-apps-2026-11-23)) |
| A1: an app approves or rejects on the owner's behalf (shared plan step 4) | done (deployed in 0.1.0; opt-ins `apply_owner_grant_approvals_schema` and `apply_owner_grant_decisions_schema`; owner approved 2026-11-23): A1-1 the default-off consent-page option, stoppable on `/account` (#555, #556); A1-2 the owner-grant tools `get_pending_action`, `approve_action` (exact plan digest) and `reject_action` under the owner page's rules, recording the grant (#557, #558); A1-3 the owner's action page names the deciding app (#559, `74322b8`); `/account`'s recent actions name it too (#562); `list_pending_actions` lists the 20 oldest pending plans across the owner's agents for such an app (#563, `a7ace2e`), and its `after` pages past them (2026-11-24) ([record](2026-11-01-owner-grants.md#apps-that-decide-for-the-owner-2026-11-23)) |
| List an agent's waiting plans (`propose-idempotency` option 2) | done (#492, `59103b2`; deployed in 0.1.0; no schema change): `GET /v1/external-actions` returns the agent's pending and approved, unexpired plans, oldest first, at most 20 ([record](2026-11-05-propose-idempotency.md#listing-waiting-plans-2026-11-17)) |
| List an agent's past actions in one read (work order 3, from Envoylet's per-ID reads of recent posts) | done (#526; deployed in 0.1.0): `GET /v1/external-actions?waiting=false` returns the 20 newest actions that no longer wait, newest first, with `publication`; no schema change ([record](2026-11-05-propose-idempotency.md#listing-past-actions-2026-11-22)). `&before=<actionId>` pages to older ones (#531; deployed in 0.1.0) ([record](2026-11-05-propose-idempotency.md#paging-past-actions-2026-11-22)) |
| The owner settles an action whose outcome is `unknown` (from #278): today it stays `unknown` and is never re-sent, but nothing records what the owner found on the platform | blocked (owner decision: #560): keeping the owner's finding needs a new table, an apply-schema release the owner has not approved; planned shape: on Weave's own owner page, the owner records "it was posted" (optional post link) or "it was not posted", kept with who decided and when; never re-sends, never changes the plan's history, and a later retry needs a new proposal and approval |
| List external actions over MCP (work order 5: MCP agents could not find a plan whose answer was lost) | done (#533; deployed in 0.1.0): `list_my_external_actions` with `waiting` and `before`, the same lists as `GET /v1/external-actions`; no schema change ([record](2026-11-05-propose-idempotency.md#listing-over-mcp-2026-11-22)) |
| Block or unblock a peer over MCP (work order 5: MCP agents could not block a peer, which HTTP agents could) | done (#537; deployed in 0.1.0): `set_contact_block`, the same `setBlocked` as `PUT`/`DELETE /v1/contacts/blocks`; no schema change ([record](2026-11-01-mcp-a2a-messages.md#blocking-over-mcp-2026-11-22)) |
| Remove a contact over MCP (work order 5: MCP agents could not remove a contact, which HTTP agents could) | done (2026-11-24; deployed in 0.1.0): `remove_contact`, the same removal as `DELETE /v1/contacts/accounts/{accountId}`, returning `removed`; no schema change ([record](2026-11-01-mcp-a2a-messages.md#removing-a-contact-over-mcp-2026-11-24)) |
| Revoke an address over MCP (work order 5: MCP agents could create a public address but never take it out of the directory, which HTTP agents could) | done (2026-11-24; deployed in 0.1.0): `revoke_my_address`, the same revocation as `DELETE /v1/addresses/{addressId}`, returning `revoked`; no schema change ([record](2026-11-01-mcp-a2a-messages.md#revoking-an-address-over-mcp-2026-11-24)) |
| Edit an address over MCP (work order 5: MCP agents could not publish, unlist or edit an address, which HTTP agents could with `PUT`) | done (2026-11-24; deployed in 0.1.0): `update_my_address`, the same replacement as `PUT /v1/addresses/{addressId}`; no schema change ([record](2026-11-01-mcp-a2a-messages.md#editing-an-address-over-mcp-2026-11-24)) |
| Read one receipt over MCP (work order 5: MCP agents had to page `list_my_outbox` to find one message's receipt, which HTTP agents read with `GET /v1/outbox/{messageId}`) | done (2026-11-24; deployed in 0.1.0): `get_my_receipt`; no schema change ([record](2026-11-01-mcp-a2a-messages.md#reading-one-receipt-over-mcp-2026-11-24)) |
| Read one contact request over MCP (work order 5: MCP agents had to page `list_my_contact_requests` to find one request, which HTTP agents read with `GET /v1/contacts/requests/{requestId}`) | done (2026-11-24; waiting for the next release): `get_my_contact_request`; no schema change ([record](2026-11-01-mcp-a2a-messages.md#reading-one-contact-request-over-mcp-2026-11-24)) |
| Read one address over MCP (work order 5: MCP agents had to page `list_my_addresses` to read one of their own addresses, which HTTP agents read with `GET /v1/addresses/{addressId}`) | in progress (2026-11-25): `get_my_address`; no schema change ([record](2026-11-01-mcp-a2a-messages.md#reading-one-address-over-mcp-2026-11-25)) |
| `notice-target` (Envoylet request): name an external-action notice's account | done (#453, `af57dfd`; deployed in 0.1.0): `provider`, `targetAccount` and `credentialRevision` on each `external-action` notice, copied from the action's own row; no schema change |
| Native X adapter (shared plan step 4) | done (#370, #372). 1/2 (#370): text and up to 4 images over OAuth 1.0a user context. 2/2 (#372): up to 1000 characters of alt text per image through `POST /2/media/metadata`, verified from X's docs on 2026-11-06 |
| Native LinkedIn adapter (shared plan step 4) | done (#373, `ae051a9`): text and up to 9 images with alt text on the owner's member feed with their own member access token; Posts and Images APIs checked against LinkedIn's docs on 2026-11-06 |
| Agent-facing reasons, limits and friction fixes (all deployed, the last in the owner's 2026-11-12 release at `b0ae6f5`) | done: `npm run test:files` rebuilds `dist/` before focused runs (#387, `ac0d539`); LinkedIn tokens up to 2000 characters, connect values up to 2048 (#388, `e73430e`); every connect value trimmed before checking, verifying and sealing (#390, `a6438fe`); a blank required Postiz setting refused at propose with `settings.<name> must not be blank`, and at execute with nothing sent for a plan approved before (#391, `6091549`); YouTube `upload_video` limits refused by field and limit (#392, `a636b12`); a post outside the format (alt over 2000, malformed file pin, `publishAt` not in UTC, missing `media`) refused by field and file index (#393, `a3b0c2f`); the 429 recovery test starts clear of an hour's end and a pinned-clock test checks `Retry-After` and the next hour's reset (#394, `fbe8684`); a malformed YouTube media reference refused by `mediaId`, `bytes` or `sha256`, the not-stored reason kept for that alone (#400, `fd135a9`); OpenAPI and MCP `failure` descriptions name `credential-unreadable` and a test checks every pre-dispatch code is named (#409, `8ff277f`); the binding-request (5 per 24 hours) and key-addition (10 per 24 hours) `429`s set `Retry-After` to the seconds until one leaves the window, the 1000-row key total sets none (#412, `de7acdb`; #413, `2d94b8a`). Deployed in 0.1.0: a text-only account (media `max` 0, Teams) describes `"media": []` and refuses blank text with `text is required` and media with `media is not supported here; send text only` (#532) |
| The owner pages offered approval of a plan whose account had been reconnected or disconnected, which could never succeed | done (#460, `a566d93`; #461, `5e31950`; deployed in 0.1.0; no API or schema change): the overview flags `accountChanged` with the confirm's own credential check, both pages show `account-changed` and only Reject, and a late approval says the account changed and to reject |
| Publication tracking stalled for an agent with 42 or more due Postiz posts: the run's update batch went over the sweep's 48-statement budget, was refused whole on every run and starved the records behind it | done (#486, `d281b8b`; deployed in 0.1.0; no API or schema change): a run reads at most 25 due records, so its worst case is exactly 48 statements (3 + 5 × 4 + 25) ([record](2026-11-04-postiz-publishing.md)). A cron-wide shared budget is not done: one budget would let cleanup starve tracking, so it needs its own design |
| Guards and agent-facing codes that no test caught (mutation scans; test and docs only unless noted) | done, each with positive controls and listed mutants in its PR body: media IDs (#401), action retention (#408), hourly limits (#414, #412, #413), the 20-waiting-plans limit (#415), signed media URLs (#416, #468), execute re-checks (#418, #426, #445, #459), the owner's confirm and reject (#424, #450, #455, #469), a frozen owner on connect (#425), the publication sweep and tracking (#427, #447, #448), media import (#428, #446), the stored-file type and per-type size reasons apps match literally (#535), each adapter's confirmation, refusal and connect checks (Telegram #429, #494; Discord #430, #493; Bluesky #432, #502; LinkedIn #433, #495; YouTube #435, #444, #490, #491; X #437; Postiz #438, #504), definite-refusal status lists (#489), visible text (#439), owner connect and decision RPC input (#440, #441, #443, #464), action timestamps (#449), `publishAt` only on Postiz (#457), the owner page's pending list (#458), propose input (#462), binding requests (#463), inbox notices (#467, #488), DPoP (#470), the credential vault (#471), key recovery codes (#472), token assertions and access tokens (#478, #483), plan expiry (#484) and the 48-statement D1 budget (#485). Product fixes among them: #450, #455 and #495 (`connectAccount` refuses an account id that cannot form a credential reference). The upload timeout for Discord files is a timing value and stays unpinned |
| Owner pages: what became of an action, and what the owner can still do | done (deployed in 0.1.0; no schema change; [record](2026-11-05-action-approval-page.md#behaviour)): the owner's own decided action reads its outcome, receipt, tracked publication, failure explanation and which action it was (#505, #506, #508, #509, #510); "Withdraw approval" before `dispatching` (#511, `215e5c7`); `/account` says when its lists were cut (#512, `e120adf`); an approved action's send-by time (#513, `b003301`); Disconnect warns how many waiting actions use the account (#514, `e7a01d4`); "View the post on the platform" for native X, Bluesky, LinkedIn and YouTube receipts (#522, `ab085f7`) and Telegram channel or supergroup messages (#525); the same address as `postUrl` on the agent's action reads (#527, `b0dc740`) and on `external-action` inbox notices |
| `notice-receipt` (Envoylet request): the platform receipt on `external-action` notices | done (#523, `d0e394c`; deployed in 0.1.0; no schema change): `receipt` (string or null, set only when `completed`); `publicationUrl` declined because a tracked Postiz URL changes after completion without a notice |
| AI-1 owned-agent limit and AI-2 owner app grant (AI Office as a second consumer, generic owner/app capabilities) | done (AI-1 #496, `e223e45`; AI-2 #498, #501, #503, `0e151b7`; deployed in 0.1.0, and AI-2's production table needs the opt-in `apply_owner_grants_schema`) ([agent binding](2026-11-01-agent-binding.md), [owner grants](2026-11-01-owner-grants.md)) |
| Dependency upgrades that need migration work (from Dependabot #542–#547, 2026-11-23; `csharp/` is in maintenance, so these are dependency upkeep only) | todo: Aspire 13.6.5 → 17.0.0 (#543–#545) makes `AddProject<TProject>` obsolete (`CS0618` under warnings-as-errors in `Weave.AppHost/Program.cs`; replacement `AddDotnetProject(name, path)` from the new `Aspire.Hosting.Dotnet`), and `CommunityToolkit.Aspire.Hosting.Dapr` has only 17.0.0 previews; `Microsoft.AspNetCore.App.Internal.Assets` stays on the 10.0.x line while `Weave.Dashboard` targets `net10.0` (#546 offered 11.0.0); `Microsoft.Extensions.AI` and `.Abstractions` 11.0.1 need `Microsoft.Extensions.DependencyInjection.Abstractions` and `Logging.Abstractions` 11 (`NU1109` against the central 10.0.15), so they move with that family; `actions/dependency-review-action` 5.0.1 replaces the pinned license-policy revision `a1d282b`, which `scripts/tests/test_dependency_license_evidence.py`, `scripts/acceptance/dependency_policy.py` and the [license records](2026-09-22-license-fork-acceptance.md) name, and its own change fails the license policy as `license-evidence-unavailable` |
| Record each Workers release (`advance-weave` release rule: the sandbox's tag push was refused, so 0.1.0 had no tag or GitHub Release) | in progress: `release-workers.yml`, triggered by each successful deploy run (the deploy keeps its read-only token) or run by hand on main, tags `workers-v<version>` at the deployed commit and publishes the CHANGELOG section as a GitHub Release, refusing a commit whose newest production deployment did not succeed and a tag already on another commit; then a manual run records 0.1.0 at `d13c809` |
| `submit-nuget` check (NETSDK1045) | blocked (owner decision: #365, `dependency-review-auto-nuget`); intermittent: GitHub's Automatic Dependency Submission (not a workflow here) fails on runners with only SDK 10.0.x and passes on ones with .NET 11, so one commit can do either; handling and `[owner-decision] dependency-review-auto-nuget` in [advance-weave](../../.claude/skills/advance-weave/SKILL.md#gate). Untested candidate fix: an explicit compatible SDK in `global.json` ([#372](https://github.com/KoalaFacts/Weave/pull/372#issuecomment-6717426980)) |
| Log the cause behind a `503 storage` answer (the 2026-11-24 demo outage left no trace of its cause) | in progress (PR #573): `problem()` logs `storage-unavailable` with the error class and a bounded D1 message; 9 unit tests; independent review 2026-11-24: 8 of 10 mutations caught, the anchor-colon survivor now has a test, the other is output-equivalent |

### Run log

- 2026-11-24 (UTC 16:52): 0.1.0 deployed to the demo from `d13c809` by production dispatch run 45442247683 with the four
  opt-ins `apply_app_webhooks_schema`, `apply_app_webhook_deliveries_schema`, `apply_owner_grant_approvals_schema` and
  `apply_owner_grant_decisions_schema`, after a manual `ci-auth-worker.yml` run on that commit (its path filter skips a
  CHANGELOG-only commit). Smoke check passed (`/openapi.json`, `/agent-start.md`, MCP handshake `weave-public` 0.1.0).

- 2026-11-11: the owner approved `schema-366`. #366's branch was merged with main in a new PR that supersedes #366,
  without its draft deploy guard, which moves to the additive-schema release-path follow-up. Not deployed.
- 2026-11-06 10:17 (Sydney):
  - Baseline on `main` `0d2d22e`, Node 24: mailbox-worker 50 + 938, auth-worker 92 + 93, all passing.
  - `submit-nuget` was the recorded next item. It turned out to be GitHub's dynamic Automatic Dependency Submission,
    not a workflow here, so it was marked blocked on a settings decision instead of attempted.
  - Opened the native X adapter (OAuth 1.0a, text and images). OAuth 2.0 was set aside: it needs a Weave-registered X
    app, and its refresh token rotates on use, which would change the sealed secret on every dispatch. Alt text was
    left out because X's v2 metadata call could not be verified from its documentation in this run.
- 2026-11-06 04:17 (Sydney):
  - Merged #367 (records index, historical banners).
  - Housekeeping, #368, merged: added `scripts/docs/check-docs.ts` with tests and a CI job; renamed the docs index to
    `docs/README.md`; fixed stale paths and claims in `docs/best-practices.md`.
  - CodeQL flagged two problems in the first checker, both fixed: a polynomial regex and incomplete sanitization. An
    independent review then found quadratic regexes still in place and a timing test that passed for the wrong reason.
    All parsing is now linear scans.
  - Opened external-action status notices (status-change notification, step 1). A push webhook was considered first
    and set aside because storing the callback address is a schema change. Server-side long-polling was also set aside:
    with D1 polling it costs more reads than client polling.
  - Baseline on `main` `5cbd784`: mailbox-worker 50 + 926, auth-worker 92 + 93, all passing.
- 2026-11-05: merged #363 and #364 (`approvalUrl` on pending actions). Opened #366 (`propose-idempotency`), which now
  waits on the owner's schema decision, and #367 (docs tidy).
