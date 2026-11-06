# Implementation records

Each record states what one change delivered, how it was verified and what it does not do. Records are history:
they describe the code when they were written and are not rewritten later, so a newer record or the code itself
wins where they differ. A record superseded by a later one says so in its first lines.

Current rules live in [AGENTS.md](../../AGENTS.md) and target semantics in [ARCHITECTURE.md](../../ARCHITECTURE.md).
Newest first within each area. Every record (or dated record section) in this directory is listed once.

## Publishing router and governed external actions (TypeScript Workers)

- 2026-11-06: [X publishing adapter](2026-11-05-publishing-router.md#native-x-adapter-2026-11-06)
- 2026-11-06: [External-action status notices on the inbox](2026-11-06-external-action-notices.md)
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

Last docs housekeeping: 2026-11-06, `weave/docs-housekeeping-2026-11-06` (adds the `check-docs` checker).

### Items

| Item | Status |
| --- | --- |
| `propose-idempotency` (Envoylet request): `Idempotency-Key` on `POST /v1/external-actions` | blocked (#366; owner decision `schema-366` on #365: approve or reject the additive `external_action_keys` table. No further schema work until then) |
| Status-change notification for apps (shared plan step 4) | 1/2 done (#369, `0d2d22e`, deployed 2026-11-05 21:37 UTC): external-action notices on the inbox. 2/2, a push webhook, is blocked: storing a callback address is a schema change and needs an owner decision first |
| Native X adapter (shared plan step 4) | 1/2 (#370): text and up to 4 images over OAuth 1.0a user context. 2/2 in progress (`weave/x-alt-text`): alt text through `POST /2/media/metadata`, verified from X's docs on 2026-11-06 |
| Native LinkedIn adapter (shared plan step 4) | todo |
| `submit-nuget` check (NETSDK1045) | blocked: it is GitHub's Automatic Dependency Submission, not a workflow in this repository, so no repository change fixes it. Owner decision: turn it off, or change its .NET setup, in Code Security settings |

### Run log

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
