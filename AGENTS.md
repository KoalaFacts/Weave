# Weave — shared contributor instructions

This is the canonical repository instruction file for coding assistants and human contributors. It holds the rules every change needs; [contributor rules in detail](docs/contributing.md) holds the rest (Governed Tools state, project layout, plugins, engineering conventions, build details, documentation maintenance) and is equally binding. Keep shared rules in these two files, not in parallel vendor-specific guides.

## Read the right source

- Read [ARCHITECTURE.md](ARCHITECTURE.md) before changing feature boundaries or behavior. It defines the independent-agent network and separate Governed Tools boundaries, not a claim that every target feature exists. Use [product positioning](docs/product-positioning.md) for what Weave is; [README.md](README.md) for onboarding; code and tests for current behavior.
- [Implementation records](docs/implementation/) for delivered scope, migrations and verification limits; the relevant sections of [best practices](docs/best-practices.md).
- The flat architecture and these rules supersede old Shared/Core/layer-first layouts in historical guides and review checklists. Keep their applicable security and testing rules; do not restore obsolete projects or paths.
- Inspect the actual branch, diff, nearby code and applicable directory instructions before editing, and preserve unrelated or concurrent work. A previous conversation, PR description or successful run is not proof about the current commit.

## Product intent and implementation boundary

Weave's primary direction is an **independent-agent network** with contact, inbox and outbox primitives. Admission, member/peer authentication, payload interpretation and collaboration belong to recipient endpoints. Public cards permit attempts without accepting them; unlisted cards and audience hints confer no membership authority. Agents may use their own direct methods without relay copies. The first increment is the opt-in HTTP/SQLite mailbox host; read its [protocol](protocol/weave-mailbox/v1/README.md) and [implementation record](docs/implementation/2026-10-07-agent-contact-mailbox-mvp.md). Do not claim hosted-agent wake, a production client E2EE adapter, physical/backup erasure or exactly-once business effects.

**Public demo sign-in is open (owner decision, 2026-11-05).** Any GitHub account may sign in to the public demo (`weave-auth-demo`) for testing. `WEAVE_TRIAL_GITHUB_SUBJECTS` is an optional allowlist that the owner may set. Do not make it mandatory again, or reintroduce a required 2–10 trial cohort, without a new explicit owner instruction. This decision supersedes the earlier #236 "never configure Everyone" trial-cohort requirement. #357 reintroduced that requirement and was reverted for this reason. A malformed allowlist still fails closed.

**Implementation focus and sibling products (owner decision, 2026-11-10).** Weave offers two primitives: messages between agents, and governed external actions (propose, owner approval of the exact plan, at-most-once dispatch, recorded outcome). The publishing router is the first adapter family of the second, not the product. New features go in the TypeScript Workers (`typescript/mailbox-worker`, `typescript/auth-worker`). Everything under `csharp/` is in maintenance: fix defects, security issues and dependencies and keep its guarantees and tests, but add no product features there without a new owner instruction. Weave is the only gate for outward effects of the sibling products AI Office (owner-facing work) and Envoylet (an agent's resources); it must not learn their concepts or grow a product-specific interface. See [product positioning](docs/product-positioning.md).

The .NET Governed Tools capability, its durable journal and approval guarantees are described in [Governed Tools current state](docs/contributing.md#governed-tools-current-state); preserve them.

## Security invariants

Preserve current token validation, workspace checks, secret handling, leak scanning, redaction, bounded output, and cancellation while implementing stronger boundaries. No alternate ungoverned execution path.

- **Identity is not context or authority.** In the target, Tenant is the security partition; Agent identity is independent of Workspace/Room. Authenticate context server-side. Submitted identifiers, resource bindings, tool discovery, and plugin installation do not confer permission.
- **Authorize the actual operation and target.** Preserve grant conditions, expiry, revocation, delegation, installation, resource, and credential constraints. Unknown/ambiguous decisions fail closed; never choose a more privileged default account behind an approved tool name.
- **Approval binds to a frozen plan.** Revalidate current access and plan validity before dispatch. Changed meaningful inputs/targets require reevaluation. Persist the attempt and mandatory local evidence before the external effect; recording failure blocks dispatch. Preserve the current bounded approval guarantees while extending them to new providers and ingress paths.
- **Unknown outcome is not failure-with-no-effect.** Timeouts/cancellation after dispatch may leave `OutcomeUnknown`. Do not blindly retry non-idempotent effects. Separate Invocation from Attempt; test concurrent resume and recovery. Do not hold a request or transaction open across a human decision.
- **Protect credentials in practice.** Never put real secrets in source, committed configuration, logs, exception bodies, fixtures, approvals, or audit records. Preserve redacted `SecretValue.ToString()`. Use references and approved brokered/short-lived access where possible. An adapter that must receive a secret is part of the trusted execution path.
- **Do not turn authentication failures into anonymous access.** `Weave:Auth:Mode=none` is the current anonymous default, not production protection. Enabled auth with invalid configuration or an unavailable provider must remain fail-closed. Built-in bearer auth is not a full OAuth/OIDC implementation.
- **Do not claim fake isolation.** In-process code is trusted; an interface/assembly loader is not a sandbox. External processes need actual credential, filesystem, network, and process restrictions. Redaction cannot guarantee freedom from prompt injection or exfiltration.

When adding an ingress, test equivalent authorization and outcome semantics through it. Administrative operations—grants, approvals, installations, credential registration, resource binding, and audit export—need authorization too.

## Build and verification

Use the .NET 11 SDK with the .NET 10 runtime and Node.js 24 or later; [build details](docs/contributing.md#build-and-verification-details) covers SDK pinning, lockfiles, TypeScript rules, test libraries, browser tests and documentation checks. For code, dependency or build changes run:

```bash
python3 -m unittest discover -s scripts/tests -v
dotnet restore csharp/Weave.slnx
dotnet restore csharp/Weave.slnx --locked-mode
dotnet build csharp/Weave.slnx --no-restore -c Release
dotnet test --solution csharp/Weave.slnx --no-build -c Release
```

For a focused test run after building:

```bash
dotnet test --project csharp/tests/Weave.Security.Tests/Weave.Security.Tests.csproj --no-build -c Release
```

Test libraries, regression and coverage rules, browser tests, warnings and the documentation checker are in [build details](docs/contributing.md#build-and-verification-details); follow them for every change.

Before completing non-trivial changes, apply the relevant [check-rules](.claude/skills/check-rules/SKILL.md) and [adversarial-review](.claude/skills/adversarial-review/SKILL.md) checklists. Before claiming a change works, follow [verification-first](.claude/skills/verification-first/SKILL.md) with [verify-weave](.claude/skills/verify-weave/SKILL.md), which maps each feature to the real boundary that exercises it. The scheduled Weave task also follows [advance-weave](.claude/skills/advance-weave/SKILL.md). These are readable repository procedures even without a tool-specific skill runner. Hooks and checklists supplement review; never fabricate review markers, independent reviewers, test results, or approvals.

## Git and completion policy

Work on the agreed feature branch or isolated workspace. Inspect status before edits and before pushing. Preserve concurrent work; use non-force updates and recheck the remote head. Do not merge to main, publish packages, create a release, deploy, change production credentials, reset persisted state, or delete upstream resources unless explicitly authorized for that operation.

Review the final diff for unintended changes. Report the files/behavior changed, exact tested commit where applicable, commands actually run, outcomes, skipped/failed checks, and remaining limits. Distinguish self-review from independent review. Deliver the repository change/PR; do not attach raw CI archives unless requested.
