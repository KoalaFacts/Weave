# Weave contributor rules in detail

These rules are as binding as [AGENTS.md](../AGENTS.md), which is the short entry point and links here. They were moved out of AGENTS.md unchanged on 2026-11-15 so the entry point stays readable; change a rule here only with evidence it is obsolete.

## Governed Tools current state

The existing **Governed Tools** Agent Control Plane remains a separate capability for agents and integrations. A Weave-hosted reasoning loop is not a product requirement. Do not make marketplaces, broad provider catalogues, model routing, email/SMS infrastructure, or a universal workflow engine prerequisites for contact/message delivery or a concrete governed use case.

Current Agent actor keys remain `{workspaceId}/{agentName}`. `csharp/src/Invocations/InvokeTool/ToolActor.cs` now authorizes adapter-normalized `tool:<name>:invoke:<operation>` scopes separately from connection permission; the registry derives invocation grants from explicit Capabilities, not tool availability. The current host still uses Orleans and the existing Agent Runtime. Portable Agent identity, Tenant/Room authority, generalized resource/installation revision constraints, and reconciliation remain target work. Read the [operation-authority migration record](implementation/2026-09-19-exact-tool-operations.md); do not restore implicit grants or automatically broaden old `tool:<name>` entries.

ToolActor now requires durable invocation/attempt admission before dispatch. The host composes an on-disk SQLite journal, not an optional in-memory fallback. Owner-scoped outcome queries use `invocation:read`; duplicate submissions return metadata without replay. This is a single-host/preserved-file increment, not cluster-wide exactly-once execution or a response-body cache. Read the [journal migration and limits](implementation/2026-09-19-durable-invocations.md). Preserve unknown outcomes and retained IDs; do not add automatic replay, silent record deletion, or a production no-op journal.

Configured approval requirements now persist waiting and independent decisions in that journal. The FileSystem adapter contributes the first target binding. Approval adds no execution grants; the original caller resubmits the same ID and request with current authority, and approval consumption is atomic with attempt admission. Unsupported required targets fail closed. Read the [approval scope and migration notes](implementation/2026-09-20-durable-approval.md). Decisions require a Human principal (the signed token `Kind`) plus the exact `tool:<name>:approve:<operation>` grant; `approval:decide` is removed and `principal:` requirements cannot be granted. Read the [human-only approval record](implementation/2026-10-29-human-approval-principals.md). Operators may require 2–3 distinct human approvers per required grant (`ApprovalQuorums`); the quorum is stored when approval is requested and one rejection rejects. Read the [quorum approval record](implementation/2026-10-29-quorum-approval.md). This is an operator/Orleans API, not a human identity system, review UI or external HTTP/MCP ingress. Do not turn opaque stored digests into unexplained approval prompts, or store old tokens for later automatic dispatch.

For a replacement, record what is preserved, deliberately changed, or removed. Start with one complete, tested path rather than scaffolding every architectural noun.

## Ownership and composition

- Top-level folders are split by language. `csharp/` holds the C#/.NET implementation (solution, `Directory.*` build files, `NuGet.config` and every .NET project); `typescript/` holds TypeScript packages (Workers, the npm Echo plugin). `protocol/`, `docs/`, `examples/`, `plugins/`, `scripts/` and `.github/` stay language-neutral at the root, and `global.json` stays at the root because the .NET SDK resolves it from the working directory. New implementations or SDKs go in a folder named for their language (`python/`, `rust/`, ...), not for a platform.
- Product source is `csharp/src/Weave.csproj`; features are **direct children of `csharp/src/`**. No `csharp/src/Weave/`, `csharp/src/Features/`, universal Core/Contracts/Kernel project, or mandatory Domain/Application/Infrastructure stack.
- A feature owns requests, results, behavior, state, data access, and endpoint adapters. Do not recreate technical-layer baskets globally or inside every slice. Queries may access their owned data directly.
- Keep public contracts with their semantic owner. Cross-feature callers use explicit public operations and owned/immutable snapshots, not private handlers, mutable entities, or another feature's storage.
- Prefer composition to base-class hierarchies. Extract an interface/component for actual variability, reuse, testing, or lifecycle needs—not to satisfy a template. Classes over 200 lines warrant a responsibility review, not artificial splitting.
- Inside `csharp/`: `hosts/` compose implementations; `extensions/` hold opt-in protocol/runtime/provider dependencies; `tools/` hold generators and the coverage DevTool; `tests/` hold .NET tests. A separate project needs a packaging, dependency, deployment, or isolation reason.
- `csharp/src/Composition` is dispatch/composition machinery, not a dumping ground for business types. Ordinary handlers must not use `IServiceProvider` as a service locator. Keep dependencies explicit, lifetimes correct, and the startup graph acyclic; do not capture scoped collaborators in singletons.
- Provider/installation identity determines routing. Safety-critical registrations must not rely on DI order or last-registration-wins. Do not import concrete provider packages into generic product code.

| Current project | Assembly / responsibility |
| --- | --- |
| `csharp/src/Weave.csproj` | `Weave.Product`; avoids collision with the CLI assembly `weave`. |
| `csharp/hosts/Weave.Host/Weave.Host.csproj` | `Weave.Silo`; existing Orleans/HTTP host. |
| `csharp/hosts/Weave.Mailbox.Host/Weave.Mailbox.Host.csproj` | `Weave.Mailbox.Host`; opt-in contact/inbox/outbox HTTP relay with no reasoning runtime. |
| `csharp/extensions/Weave.Mailboxes.Sqlite/Weave.Mailboxes.Sqlite.csproj` | Relay persistence; provider dependency stays outside product contracts. |
| `csharp/extensions/Weave.AgentRuntime/Weave.AgentRuntime.csproj` | `Weave.Agents`; existing reasoning/model integration. |
| `csharp/tools/Weave.SourceGen/Weave.SourceGen.csproj` | Source generation; targets `netstandard2.0`. |

Keep retained namespaces and assembly names unless the task explicitly includes their migration. Physical layout changes do not prove binary, wire, or persisted-state compatibility.

## Plugins and resources

A PluginDefinition describes contributions and requested permissions. A PluginInstallation is the configured instance with its own identity, credentials, actual grants, readiness, and lifecycle. Keep those concepts separate when implementing the target.

**Internal / External is installation lifecycle ownership**, not trust or protocol. MCP/CLI/HTTP/gRPC are interfaces, OpenAPI is a description, and process/container/WASM are hosting choices. Do not infer implementation support from these examples or introduce an enum for every future provider.

Preserve installation-scoped operation identity and schemas; new/discovered operations are not automatically granted. Schema/behavior changes require reevaluation. Do not silently redirect an approved plan after an upgrade.

Resources owns generic lifecycle and binding semantics; mailbox/telephone/browser/vendor-specific behavior belongs in extensions. Start with imports/references before provisioning. Binding is not ownership or an unrestricted grant. Disable/uninstall/Agent deletion must not implicitly delete external assets or approval/audit history. Reconciliation is not replay of a non-idempotent command.

## Engineering conventions

- Follow `.editorconfig` and `csharp/tests/.editorconfig`: UTF-8, LF where specified, four-space C#, and two-space JSON/YAML/XML. .NET interfaces keep the `I` prefix. Extension classes follow the existing `ExtensionsToXXX` convention; Blazor behavior goes in `.razor.cs` code-behind.
- Use existing branded IDs and typed outcomes. Return expected validation/operation failures rather than throwing for normal control flow. Preserve meaningful error categories; never swallow exceptions or report failed/unknown operations as successful.
- Inject `TimeProvider` for behavioral time. Propagate cancellation at async boundaries, observe background-task faults, and avoid fire-and-forget actor calls.
- Prefer executable/argument arrays for new CLI operations. Treat shell access as a separate privilege. Drain stdout/stderr concurrently, bound retained output, redact it, and handle process cleanup. A zero exit code alone need not prove business success.
- Prefer supported source-generated JSON, regex, logging, IDs, and registrations. Keep platform-specific APIs guarded. Native AOT is verified per host, not assumed for all plugins or arbitrary dynamic DLL loading.
- Preserve zero-argument interactive CLI mode and explicit scriptable commands. Keep the `weave` command, `~/.weave/` configuration namespace, existing 94xx ports, and storage defaults unless their change is in scope. Do not invent default credentials.
- Update real call sites for deliberate pre-1.0 changes rather than inventing Legacy classes, dual keys, or compatibility shims. This is not permission to reset user data or ignore external consumers.
- Retain `Apache-2.0` licensing; do not change licensing as incidental cleanup.

### Generator and persistence traps

The product sets `GenerateCqrsRegistration=false` but still uses branded-ID generation. The executable Host emits the CQRS registry across referenced features; avoid duplicate public registries. Branded IDs live with their features; Orleans serialization adapters stay with the consumer host.

Persisted Orleans field IDs are append-only: never renumber or reuse them. Keep actors constructor-injected and directly testable. A namespace, assembly, or actor-key change needs an explicit stored-state/wire compatibility decision and evidence.

## Build and verification details

The commands themselves are in [AGENTS.md](../AGENTS.md#build-and-verification).

Run commands from the repository root with the .NET 11 SDK and the .NET 10 runtime, and check `dotnet --version`. `csharp/src/Weave.csproj`, the CLI and their focused tests target `net10.0;net11.0`; other projects target .NET 10. `global.json` uses the baseline `10.0.400` with `rollForward: latestMajor` and `allowPrerelease: false`, so the newest installed GA SDK (.NET 11) is selected; the baseline matches the Roslyn version referenced by the source generator. Shared CI setup installs the `10.x` and `11.x` GA channels (`dotnet-next-quality` defaults to `ga`); `WeaveILLinkTasksVersion` and `WeaveILLinkTasksNextVersion` in `csharp/Directory.Packages.props` pin the ILLink pack per target so lockfiles do not move with the SDK; bump them, and regenerate lockfiles, when CI moves to a newer SDK (see `csharp/Directory.Build.targets`). CI does not install the baseline as an exact SDK. Keep locked restore and security auditing enabled; an SDK-driven dependency graph change requires deliberate lockfile regeneration and verification. Most projects target .NET 10. Package versions are centralized in `csharp/Directory.Packages.props`, except the explicitly opted-out generator project. Regenerate affected lockfiles after dependency changes; do not hand-edit their hashes.

Node.js 24+ is the minimum for every Node package, tool, example and test fixture; CI selects Node 24. Command-process fixtures run their `.ts` worker directly with no npm install.

Write new Node tools and tests in TypeScript (`.ts`), using Node 24 native type stripping and a separate type-check gate. Never add or generate `.cjs`, `.mjs`, `.cts` or `.mts` files. Keep explicit import extensions and erasable TypeScript syntax. Preserve executed data-migration content bytes, checksums and identities. A source-file rename must retain the original checksum inputs and prove that completed records are recognized without rerunning conversion. Historical migration artifacts are excluded from annotation-only edits; ordinary tools are checked separately from runtime tests.

Tests use **xunit.v3, Shouldly, and NSubstitute**. Do not introduce FluentAssertions or another .NET test framework. Test names use `Method_Condition_ExpectedResult`. Use `NullLogger<T>.Instance` for internal types that a substitute cannot proxy. Python checks use the standard-library `unittest` toolchain.

Real browser tests use the pinned official Playwright SDKs and their managed Chromium channel. Follow [browser testing](browser-testing.md) for installation and focused commands. Keep browser startup, contexts, locators, uploads and cleanup in Playwright; do not restore manual Chrome processes, CDP clients, executable discovery or debugging-port polling. Preserve origin restrictions and protected-browser assertions. Missing browsers or unavailable sandbox support are failed prerequisites, never successful UI acceptance or a reason to weaken the gate.

Add a regression that fails for the intended reason before fixing behavior; include positive controls, denial/error paths, and cancellation as applicable. Mocks alone do not prove persistence, external effects, isolation, or concurrency. Verify those claims at the real boundary. Check skipped tests and prerequisites such as Python, symlink privileges, or Docker where the affected suite needs them.

Keep warnings-as-errors and NuGet audit enabled. Do not add vulnerability suppression, weaken assertions, remove tests, or expand formatting exclusions to obtain a green run. Targeted tests are not the full suite. A previous successful build or a diagnostic build with relaxed warnings is not release evidence. Do not claim coverage success from test counts; the coverage ownership/threshold path needs its own verification.

For documentation-only changes, check rendered structure, relative links/anchors, referenced source paths, command/option definitions, and current-versus-target claims. Verify instruction bridges do not duplicate rules or create import cycles. Run the documentation checker (`cd scripts/docs && npm ci && npm run typecheck && npm test && npm run check`), which fails on broken relative links/anchors, docs under `docs/` unreachable from the [docs index](README.md), and repository paths or npm scripts named in current docs that do not exist; record a deliberate exception in `scripts/docs/allowances.json` with its reason instead of weakening the checker. Run other relevant repository checks; a .NET rebuild is not evidence that prose is accurate. Say when documented commands were source-checked but not executed.

## Documentation maintenance and agent entry points

- **README.md** serves prospective users and contributors: explain the problem, first useful workflow, current capabilities, safe onboarding, and limits. Do not substitute an architecture catalogue or unverified competitor comparisons for user value.
- **ARCHITECTURE.md** owns target semantics. Do not copy its full model into onboarding or present proposed integrations/commands as available. Keep current behavior and roadmap visibly separate.
- **AGENTS.md** owns the shared rules every change needs; **docs/contributing.md** owns the detailed rules and is equally binding. Put detailed, task-specific explanation in the relevant documentation; avoid duplicated rules, historical test totals, and stale run IDs here.
- **CLAUDE.md** is deliberately only `@AGENTS.md` and `@docs/contributing.md`, so Claude Code loads both binding files. The [Claude Code memory documentation](https://code.claude.com/docs/en/memory), checked on 2026-09-19, documents this import rather than native AGENTS.md discovery. Keep the compatibility entry point until automatic discovery is documented and verified in the supported client. Launch from the repository root and inspect `/context` to check loaded instructions; do not assume a bridge was loaded merely because the file exists.
- **.github/copilot-instructions.md** directs Copilot to these shared rules. Do not maintain a competing product brief there. Claude-specific hooks and skills in `.claude/` remain separate mechanisms; consolidating instructions is not a reason to delete them.
