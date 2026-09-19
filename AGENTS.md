# Weave — shared contributor instructions

This is the canonical repository instruction file for coding assistants. Human contributors use the same architecture, verification, and change-safety rules. Keep shared rules here, not in parallel vendor-specific guides.

## Read the right source

- Read [ARCHITECTURE.md](ARCHITECTURE.md) before changing feature boundaries or behavior. It defines the Agent Control Plane target, not a claim that every target feature exists.
- Use [README.md](README.md) for product positioning and onboarding; use code and tests to establish current behavior.
- Consult [implementation records](docs/implementation/) for delivered scope, migrations, and verification limits. Read relevant sections of [best practices](docs/best-practices.md) and the affected feature, not every historical document on every task.
- The flat architecture and this file supersede old Shared/Core/layer-first layouts in historical guides and review checklists. Preserve their applicable security/testing rules; do not restore obsolete projects or paths.
- Inspect the actual branch, diff, nearby code, and applicable directory instructions before editing. Preserve unrelated or concurrent work. A previous conversation, PR description, or successful run is not proof about the current commit.

## Product intent and implementation boundary

Weave is an **Agent Control Plane**. The first profile is **Governed Tools** for existing agents and integrations. A Weave-hosted reasoning loop is not a product requirement. Do not make marketplaces, broad provider catalogues, model routing, email/SMS infrastructure, or a universal workflow engine prerequisites for a concrete governed use case.

Current Agent actor keys remain `{workspaceId}/{agentName}`. `src/Invocations/InvokeTool/ToolActor.cs` now authorizes adapter-normalized `tool:<name>:invoke:<operation>` scopes separately from connection permission; the registry derives invocation grants from explicit Capabilities, not tool availability. The current host still uses Orleans and the existing Agent Runtime. Portable Agent identity, Tenant/Room authority, resource/installation revision constraints, durable approval/attempts, and generalized reconciliation remain target work. Read the [operation-authority migration record](docs/implementation/2026-09-19-exact-tool-operations.md); do not restore implicit grants or automatically broaden old `tool:<name>` entries.

For a replacement, record what is preserved, deliberately changed, or removed. Start with one complete, tested path rather than scaffolding every architectural noun.

## Ownership and composition

- Product source is `src/Weave.csproj`; features are **direct children of `src/`**. No `src/Weave/`, `src/Features/`, universal Core/Contracts/Kernel project, or mandatory Domain/Application/Infrastructure stack.
- A feature owns requests, results, behavior, state, data access, and endpoint adapters. Do not recreate technical-layer baskets globally or inside every slice. Queries may access their owned data directly.
- Keep public contracts with their semantic owner. Cross-feature callers use explicit public operations and owned/immutable snapshots, not private handlers, mutable entities, or another feature's storage.
- Prefer composition to base-class hierarchies. Extract an interface/component for actual variability, reuse, testing, or lifecycle needs—not to satisfy a template. Classes over 200 lines warrant a responsibility review, not artificial splitting.
- `hosts/` compose implementations; `extensions/` hold opt-in protocol/runtime/provider dependencies; `tools/` hold generators; `tests/` hold .NET tests. A separate project needs a packaging, dependency, deployment, or isolation reason.
- `src/Composition` is dispatch/composition machinery, not a dumping ground for business types. Ordinary handlers must not use `IServiceProvider` as a service locator. Keep dependencies explicit, lifetimes correct, and the startup graph acyclic; do not capture scoped collaborators in singletons.
- Provider/installation identity determines routing. Safety-critical registrations must not rely on DI order or last-registration-wins. Do not import concrete provider packages into generic product code.

| Current project | Assembly / responsibility |
| --- | --- |
| `src/Weave.csproj` | `Weave.Product`; avoids collision with the CLI assembly `weave`. |
| `hosts/Weave.Host/Weave.Host.csproj` | `Weave.Silo`; existing Orleans/HTTP host. |
| `extensions/Weave.AgentRuntime/Weave.AgentRuntime.csproj` | `Weave.Agents`; existing reasoning/model integration. |
| `tools/Weave.SourceGen/Weave.SourceGen.csproj` | Source generation; targets `netstandard2.0`. |

Keep retained namespaces and assembly names unless the task explicitly includes their migration. Physical layout changes do not prove binary, wire, or persisted-state compatibility.

## Security invariants

Preserve current token validation, workspace checks, secret handling, leak scanning, redaction, bounded output, and cancellation while implementing stronger boundaries. No alternate ungoverned execution path.

- **Identity is not context or authority.** In the target, Tenant is the security partition; Agent identity is independent of Workspace/Room. Authenticate context server-side. Submitted identifiers, resource bindings, tool discovery, and plugin installation do not confer permission.
- **Authorize the actual operation and target.** Preserve grant conditions, expiry, revocation, delegation, installation, resource, and credential constraints. Unknown/ambiguous decisions fail closed; never choose a more privileged default account behind an approved tool name.
- **Approval binds to a frozen plan.** For the target invocation slices, revalidate current access and plan validity before dispatch. Changed meaningful inputs/targets require reevaluation. Persist the attempt and mandatory local evidence before the external effect; recording failure blocks dispatch.
- **Unknown outcome is not failure-with-no-effect.** Timeouts/cancellation after dispatch may leave `OutcomeUnknown`. Do not blindly retry non-idempotent effects. Separate Invocation from Attempt; test concurrent resume and recovery. Do not hold a request or transaction open across a human decision.
- **Protect credentials in practice.** Never put real secrets in source, committed configuration, logs, exception bodies, fixtures, approvals, or audit records. Preserve redacted `SecretValue.ToString()`. Use references and approved brokered/short-lived access where possible. An adapter that must receive a secret is part of the trusted execution path.
- **Do not turn authentication failures into anonymous access.** `Weave:Auth:Mode=none` is the current anonymous default, not production protection. Enabled auth with invalid configuration or an unavailable provider must remain fail-closed. Built-in bearer auth is not a full OAuth/OIDC implementation.
- **Do not claim fake isolation.** In-process code is trusted; an interface/assembly loader is not a sandbox. External processes need actual credential, filesystem, network, and process restrictions. Redaction cannot guarantee freedom from prompt injection or exfiltration.

When adding an ingress, test equivalent authorization and outcome semantics through it. Administrative operations—grants, approvals, installations, credential registration, resource binding, and audit export—need authorization too.

## Plugins and resources

A PluginDefinition describes contributions and requested permissions. A PluginInstallation is the configured instance with its own identity, credentials, actual grants, readiness, and lifecycle. Keep those concepts separate when implementing the target.

**Internal / External is installation lifecycle ownership**, not trust or protocol. MCP/CLI/HTTP/gRPC are interfaces, OpenAPI is a description, and process/container/WASM are hosting choices. Do not infer implementation support from these examples or introduce an enum for every future provider.

Preserve installation-scoped operation identity and schemas; new/discovered operations are not automatically granted. Schema/behavior changes require reevaluation. Do not silently redirect an approved plan after an upgrade.

Resources owns generic lifecycle and binding semantics; mailbox/telephone/browser/vendor-specific behavior belongs in extensions. Start with imports/references before provisioning. Binding is not ownership or an unrestricted grant. Disable/uninstall/Agent deletion must not implicitly delete external assets or approval/audit history. Reconciliation is not replay of a non-idempotent command.

## Engineering conventions

- Follow `.editorconfig` and `tests/.editorconfig`: UTF-8, LF where specified, four-space C#, and two-space JSON/YAML/XML. .NET interfaces keep the `I` prefix. Extension classes follow the existing `ExtensionsToXXX` convention; Blazor behavior goes in `.razor.cs` code-behind.
- Use existing branded IDs and typed outcomes. Return expected validation/operation failures rather than throwing for normal control flow. Preserve meaningful error categories; never swallow exceptions or report failed/unknown operations as successful.
- Inject `TimeProvider` for behavioral time. Propagate cancellation at async boundaries, observe background-task faults, and avoid fire-and-forget actor calls.
- Prefer executable/argument arrays for new CLI operations. Treat shell access as a separate privilege. Drain stdout/stderr concurrently, bound retained output, redact it, and handle process cleanup. A zero exit code alone need not prove business success.
- Prefer supported source-generated JSON, regex, logging, IDs, and registrations. Keep platform-specific APIs guarded. Native AOT is verified per host, not assumed for all plugins or arbitrary dynamic DLL loading.
- Preserve zero-argument interactive CLI mode and explicit scriptable commands. Keep the `weave` command, `~/.weave/` configuration namespace, existing 94xx ports, and storage defaults unless their change is in scope. Do not invent default credentials.
- Update real call sites for deliberate pre-1.0 changes rather than inventing Legacy classes, dual keys, or compatibility shims. This is not permission to reset user data or ignore external consumers.
- Retain `MIT OR AGPL-3.0-or-later` licensing; do not change licensing as incidental cleanup.

### Generator and persistence traps

The product sets `GenerateCqrsRegistration=false` but still uses branded-ID generation. The executable Host emits the CQRS registry across referenced features; avoid duplicate public registries. Branded IDs live with their features; Orleans serialization adapters stay with the consumer host.

Persisted Orleans field IDs are append-only: never renumber or reuse them. Keep actors constructor-injected and directly testable. A namespace, assembly, or actor-key change needs an explicit stored-state/wire compatibility decision and evidence.

## Build and verification

Run commands from the repository root. Use `global.json` and check `dotnet --version`; its `latestPatch` policy allows SDK patch roll-forward. Most projects target .NET 10. Package versions are centralized in `Directory.Packages.props`, except the explicitly opted-out generator project. Regenerate affected lockfiles after dependency changes; do not hand-edit their hashes.

For code, dependencies, or build changes:

```bash
python3 -m unittest discover -s scripts/tests -v
dotnet restore Weave.slnx
dotnet restore Weave.slnx --locked-mode
dotnet build Weave.slnx --no-restore -c Release
dotnet test --solution Weave.slnx --no-build -c Release
```

For a focused test run after building:

```bash
dotnet test --project tests/Weave.Security.Tests/Weave.Security.Tests.csproj --no-build -c Release
```

Tests use **xunit.v3, Shouldly, and NSubstitute**. Do not introduce FluentAssertions or another .NET test framework. Test names use `Method_Condition_ExpectedResult`. Use `NullLogger<T>.Instance` for internal types that a substitute cannot proxy. Python checks use the standard-library `unittest` toolchain.

Add a regression that fails for the intended reason before fixing behavior; include positive controls, denial/error paths, and cancellation as applicable. Mocks alone do not prove persistence, external effects, isolation, or concurrency. Verify those claims at the real boundary. Check skipped tests and prerequisites such as Python, symlink privileges, or Docker where the affected suite needs them.

Keep warnings-as-errors and NuGet audit enabled. Do not add vulnerability suppression, weaken assertions, remove tests, or expand formatting exclusions to obtain a green run. Targeted tests are not the full suite. A previous successful build or a diagnostic build with relaxed warnings is not release evidence. Do not claim coverage success from test counts; the coverage ownership/threshold path needs its own verification.

For documentation-only changes, check rendered structure, relative links/anchors, referenced source paths, command/option definitions, and current-versus-target claims. Verify instruction bridges do not duplicate rules or create import cycles. Run relevant repository checks; a .NET rebuild is not evidence that prose is accurate. Say when documented commands were source-checked but not executed.

Before completing non-trivial changes, apply the relevant [check-rules](.claude/skills/check-rules/SKILL.md) and [adversarial-review](.claude/skills/adversarial-review/SKILL.md) checklists. These are readable repository procedures even without a tool-specific skill runner. Hooks and checklists supplement review; never fabricate review markers, independent reviewers, test results, or approvals.

## Git and completion policy

Work on the agreed feature branch or isolated workspace. Inspect status before edits and before pushing. Preserve concurrent work; use non-force updates and recheck the remote head. Do not merge to main, publish packages, create a release, deploy, change production credentials, reset persisted state, or delete upstream resources unless explicitly authorized for that operation.

Review the final diff for unintended changes. Report the files/behavior changed, exact tested commit where applicable, commands actually run, outcomes, skipped/failed checks, and remaining limits. Distinguish self-review from independent review. Deliver the repository change/PR; do not attach raw CI archives unless requested.

## Documentation maintenance and agent entry points

- **README.md** serves prospective users and contributors: explain the problem, first useful workflow, current capabilities, safe onboarding, and limits. Do not substitute an architecture catalogue or unverified competitor comparisons for user value.
- **ARCHITECTURE.md** owns target semantics. Do not copy its full model into onboarding or present proposed integrations/commands as available. Keep current behavior and roadmap visibly separate.
- **AGENTS.md** owns shared development instructions. Put detailed, task-specific explanation in the relevant documentation; avoid duplicated rules, historical test totals, and stale run IDs here.
- **CLAUDE.md** is deliberately only `@AGENTS.md`. The [Claude Code memory documentation](https://code.claude.com/docs/en/memory), checked on 2026-09-19, documents this import rather than native AGENTS.md discovery. Keep the compatibility entry point until automatic discovery is documented and verified in the supported client. Launch from the repository root and inspect `/context` to check loaded instructions; do not assume a bridge was loaded merely because the file exists.
- **.github/copilot-instructions.md** directs Copilot to these shared rules. Do not maintain a competing product brief there. Claude-specific hooks and skills in `.claude/` remain separate mechanisms; consolidating instructions is not a reason to delete them.
