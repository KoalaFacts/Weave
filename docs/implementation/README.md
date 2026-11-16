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

Last docs housekeeping: 2026-11-16 (Sydney).

### Items

| Item | Status |
| --- | --- |
| `propose-idempotency` (Envoylet request): `Idempotency-Key` on `POST /v1/external-actions` | done, deployed (#421, `66482d4`; #422; release path #423; supersedes #366): the owner's release run 43154439279 at `b0ae6f5` applied the approved table on 2026-11-12 04:24 UTC ([record](2026-11-05-propose-idempotency.md#release-path-applying-the-table-to-production-2026-11-12)) |
| Status-change notification for apps (shared plan step 4) | 1/2 done (#369, `0d2d22e`, deployed 2026-11-05 21:37 UTC): external-action notices on the inbox. 2/2, a push webhook, is blocked: storing a callback address is a schema change and needs an owner decision first |
| `notice-target` (Envoylet request): name an external-action notice's account | done (#453, `af57dfd`; waiting for the owner's deploy): `provider`, `targetAccount` and `credentialRevision` on each `external-action` notice, copied from the action's own row; no schema change |
| Native X adapter (shared plan step 4) | 1/2 (#370): text and up to 4 images over OAuth 1.0a user context. 2/2 (#372): up to 1000 characters of alt text per image through `POST /2/media/metadata`, verified from X's docs on 2026-11-06 |
| Native LinkedIn adapter (shared plan step 4) | done (#373, `ae051a9`): text and up to 9 images with alt text on the owner's member feed with their own member access token; Posts and Images APIs checked against LinkedIn's docs on 2026-11-06 |
| Agent-facing reasons, limits and friction fixes (all deployed, the last in the owner's 2026-11-12 release at `b0ae6f5`) | done: `npm run test:files` rebuilds `dist/` before focused runs (#387, `ac0d539`); LinkedIn tokens up to 2000 characters, connect values up to 2048 (#388, `e73430e`); every connect value trimmed before checking, verifying and sealing (#390, `a6438fe`); a blank required Postiz setting refused at propose with `settings.<name> must not be blank`, and at execute with nothing sent for a plan approved before (#391, `6091549`); YouTube `upload_video` limits refused by field and limit (#392, `a636b12`); a post outside the format (alt over 2000, malformed file pin, `publishAt` not in UTC, missing `media`) refused by field and file index (#393, `a3b0c2f`); the 429 recovery test starts clear of an hour's end and a pinned-clock test checks `Retry-After` and the next hour's reset (#394, `fbe8684`); a malformed YouTube media reference refused by `mediaId`, `bytes` or `sha256`, the not-stored reason kept for that alone (#400, `fd135a9`); OpenAPI and MCP `failure` descriptions name `credential-unreadable` and a test checks every pre-dispatch code is named (#409, `8ff277f`); the binding-request (5 per 24 hours) and key-addition (10 per 24 hours) `429`s set `Retry-After` to the seconds until one leaves the window, the 1000-row key total sets none (#412, `de7acdb`; #413, `2d94b8a`) |
| The owner pages offered approval of a plan whose account had been reconnected or disconnected, which could never succeed | done (#460, `a566d93`; #461, `5e31950`; waiting for the owner's deploy; no API or schema change): the overview flags `accountChanged` with the confirm's own credential check, both pages show `account-changed` and only Reject, and a late approval says the account changed and to reject |
| Guards and agent-facing codes that no test caught (mutation scans; test and docs only unless noted) | done, each with positive controls: 404 `unavailable` for swept, another agent's, unknown and malformed media IDs (#401); actions aged 400 days still read `200` after the cron (#408); hourly-limit tests wait out an hour's end via `clearOfUtcHourEnd` (#414); the 20-waiting-actions limit, now also named in MCP and Agent Start (#415, `4f2c2ce`); the signed Postiz download URL's expiry, media ID and extra parameters (#416); execute's `owner-changed` (#418), credential-platform and contract-revision re-checks (#426); the owner's confirm and reject `plan-changed`, `expired` and `not-pending` (#424); a frozen owner on connect (#425); the publication sweep's five-agents-per-run bound and 6-hour backoff cap (#427); media import's local-name, Content-Length, early cut-off and identity-encoding guards (#428); Telegram's (#429), Discord's (#430), LinkedIn's (#433), YouTube's (#435), X's (#437) and Postiz's (#438) answer-confirmation, refusal, stored-file and connect checks; Bluesky's answer-confirmation, refusal, session, stored-file and upload checks (#432). The shared visible-text rule for names and stored platform text: every hidden character class, the whitespace/ignorable-only refusal and the excerpt steps (#439). The two owner connect RPCs' input validation: `connectAccount`'s form-shape, per-field and printable/2048 value checks and `connectExternalCredential`'s seven-key, code, verifier, provider and https-redirect checks, each refused before any platform call (#440). The owner-decision RPC's `planDigest` and `keysDigest` shape checks (#441). Its `revoke-grant` and `revoke-credential` shape checks; the fresh-sign-in set, skew, window and unknown-kind guards were confirmed killed (#443). YouTube uploads of stored QuickTime and WebM video, sent as `video/*` on both requests (#444). Execute's refusal of a plan whose adapter the deploy no longer runs (`plan-changed`, nothing sent) (#445). Media import's refusal shape: every unsafe-URL, source and unreachable-source refusal is 400 `invalid` on field `url` (#446). A publication's `checkedAt`: stamped only when Postiz answers a listing, kept through a failed one, null when never asked (#447). The publication tracking contract: re-checks from 15 minutes, doubling to the 6-hour cap, and the last check after 7 days since the post was due (#448). An action's `decidedAt` (set only by approve or reject, never moved by execute) and `completedAt` (set only for `completed`, `failed` including a pre-dispatch refusal, and `unknown`), and `dispatchedAt` (stamped before the platform answers, never moved by the outcome) (#449). The owner's confirm re-checks the credential's platform as execute does, so a re-platformed credential is `plan-changed` before approval (a one-line product fix, #450). The owner's confirm also re-checks the adapter's presence, its contract revision and the recomputed digest, so approve and execute refuse the same plans (#455). Only Postiz accepts `publishAt`, which Envoylet relies on for deadlines: every other adapter refuses it at propose and its description omits it (#457). The owner page's pending-plan list: every live pending plan of each owned agent (a full agent does not crowd out another), never an expired one or another human's (#458). A provider call whose outcome could not be recorded stays `dispatching`: no later execute, plan expiry or cron sweep sends it again or reports it finished, which Envoylet's stalled-send note relies on (#459). Propose's input refusals by field and reason: a malformed `credentialRef` (over 64 characters, empty, a space, a slash, not a string) gets the format reason, never "must name a live credential"; non-string, whitespace-only and over-8000-character content; the body-shape refusal names no field; a 64-character reference and 8000 characters are admitted; nothing is stored (#462, `285e6bb`). Owner binding requests: a cooling key's cancel of a live pending request is 409 `cooling` and leaves it pending, while it still reads the request and gets a settled one back unchanged; a pending request reads `expired` from the exact `expiresAt` tick, `pending` one tick earlier; a fractional `iat` is refused (#463, `d8b5703`). Owner connect: a YouTube sign-in time more than a minute ahead or more than ten minutes old is `stale-authentication` at the exact edges, and a fractional one is an unknown form, before any code exchange; `connectAccount` also refuses a sign-in time more than a minute ahead; a revoked credential is no longer listed to its agent, and reconnecting it clears the revocation and lists the new revision (#464, `5276fe3`). External-action inbox notices at exact ticks on a pinned clock: a last change exactly at the 24-hour window's start is left out and one tick later is reported; a live plan untouched since before the window is reported at its expiry only once that expiry is inside the window; a plan expiring exactly now reads `expired`, ranked by that expiry, and one a tick later is not reported (#467). Signed media URLs: each signed upload and download URL is valid until the millisecond before its signed expiry and 404 from that instant, an upload URL lives 15 minutes, a wrong Content-Type or Content-Length is refused on that header, the upload compares only the media type, the download is `nosniff`, and maintenance refuses minting and receiving (#468). The owner's reject of a governed action needs no fresh sign-in, like rejecting a binding or key request: a day-old session still rejects it and nothing is sent (#469, `5970d04`). DPoP proofs (`verifyDpop`, every agent-token and agent-API request): a `jti` with a control character (0x1F) or DEL (0x7F) is refused while space and `~` are accepted; an `htu` with only a password in its userinfo is refused; a non-string `htu`, including a one-element array that `new URL()` would stringify to the target, is refused (#470, `2b116ca`). The credential vault (AES-256-GCM sealing of every stored platform secret): each binding member (agent, reference, provider, revision) is bound and framed, so a secret never opens under another reference or provider; each seal draws a fresh IV; a flipped IV, ciphertext or tag byte and a truncated seal are refused; a key that is not exactly 32 bytes of base64url (16 and 24 included, which AES-GCM would otherwise accept) is refused (#471, `c2666d7`). Owner-issued key recovery codes (`POST /v1/key-requests`): a code is accepted until the tick before its 10-minute expiry and refused from that tick; a code issued before the agent passed to another human is refused, and the new owner's code replaces it; a code reissued after use is accepted; the agent's own revoked key is 409 `key-in-use` and its own live key only spends the code; the 101st live key is 429 `capacity`; each refusal leaves the code unspent (PR pending). The upload timeout for Discord files is a timing value and stays unpinned |
| `submit-nuget` check (NETSDK1045) | intermittent: GitHub's Automatic Dependency Submission (not a workflow here) fails on runners with only SDK 10.0.x and passes on ones with .NET 11, so one commit can do either; handling and `[owner-decision] dependency-review-auto-nuget` in [advance-weave](../../.claude/skills/advance-weave/SKILL.md#gate). Untested candidate fix: an explicit compatible SDK in `global.json` ([#372](https://github.com/KoalaFacts/Weave/pull/372#issuecomment-6717426980)) |

### Run log

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
