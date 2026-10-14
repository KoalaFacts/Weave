# Demo Agent Start and non-secret invitations

## Delivered scope

The Worker homepage exposes `/agent-start.md` in server-rendered HTML before account setup. The optional stdio MCP reads the same checked-in guide and returns it through `demo_guide.instructions`, alongside safe readiness metadata. Its former linear `steps` list is replaced by the canonical state-based instructions. Existing tool schemas, HTTP mailbox operations, recipient authority, credential rules and retention remain unchanged.

The rendered A/B copy prompts point to that guide instead of independently prescribing fixed ping/pong text. The original browser walkthrough remains a manual example, not the agent execution contract.

The contact-card panel reads only the current account's existing addresses. It emits an allowlisted non-secret invitation with origin, recipient IDs and an explicit test runId. Preparation and copying never register, publish, request, accept or send. Copies/rebuilds preserve the same run unless the user explicitly starts another test or selects a different address. Clearing/switching accounts invalidates the panel and stale reads. No persistent browser storage was added.

Client state names and runId/replyTo are reporting/application-text conventions, not new HTTP request fields or a workflow API. Hosts must retain real checkpoints/envelopes and supply receiving execution. This increment does not implement hosted-agent wake, an autonomous loop, a durable client store, E2EE, or independent reasoning-agent acceptance testing.

## Release path

The existing manual `Deploy Weave Worker` workflow remains available with repository/production configuration scopes. An explicit push of an already-merged commit to `publish/demo` supplies a git-based release signal using the existing repository configuration; normal main pushes do not deploy. The verify job checks that the selected SHA is checked out and is an ancestor of main before the existing Worker tests, audit and C# compatibility checks. No branch force-update is needed or authorized by this change.

Both deployment jobs still use the existing resource-inspecting deployment script, retain the production environment gate where applicable, and then make anonymous read-only requests to the fixed demo origin. The published guide must exactly match the checked-out source; the homepage must expose the agent entry and invitation controls. Failure is reported as verification failure, not silently retried or called success. No account/message is created on the public demo by this verification.

## Verification

Tests cover inert discovery, secret exclusion, stable run IDs, explicit new runs, address ownership/effective bounds, bounded pagination, stale-session handling, clipboard failure, shared HTTP/MCP guide bytes, current API path references, per-response CSP, real protected-browser interactions, and post-deploy mismatch/response failures. Local pure-JavaScript tests do not substitute for the repository's Worker/D1/MCP, real-browser, compatibility or full CI results. Record those results on the PR and workflow for the exact commit.
