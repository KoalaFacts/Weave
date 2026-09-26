# Weave

[![CI](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml)
[![Security Scan](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/scan-security.yml)

**An open-source control plane for AI agents.**

Connect your agents. Control what they can do. See what they did.

Weave is building the layer between an agent's intent and its access to real systems: which operation it may perform, on whose account, against which resource, with whose approval, and with what record of the outcome.

The first focus is **Governed Tools**: bring existing tools under explicit authorization without requiring people to replace their agents or adopt a new reasoning framework. C#/.NET is the reference implementation; the intended integration boundary is language-neutral.

> **Early development, pre-1.0.** The repository contains a working workspace-oriented runtime and tool connectors. The complete control-plane path described below is the target, not a shipping guarantee. Start with [current capabilities](#what-works-today) and the [development build](#try-the-current-development-build).

## Why Weave

Giving an agent access to an integration is not the same as deciding what it may do through that integration. Reading a repository, opening a pull request, and merging it are different permissions—even when they use the same account and tool server.

Weave is for developers connecting agents to real services and teams that need a consistent place to make those decisions. Its intended role is to connect an agent's identity and working context to a specific, authorized action—not to decide how the agent thinks.

Consider a coding agent working on a customer repository:

| Request | Intended control-plane behavior |
| --- | --- |
| Read a file | Allow only the permitted repository and operation. |
| Open a pull request | Obtain approval for the exact repository, branch, and proposed change when policy requires it. |
| Merge the pull request | Deny unless the agent has a separate applicable grant. |
| Retry after a connection drops | Determine whether the first attempt took effect before risking a duplicate action. |

**This is a design example, not a supported policy file or a claim of a ready-made GitHub integration.** It explains the distinction Weave is working toward: access to a tool must not become blanket authority over everything behind it.

## What works today

The current code is the starting point for the control plane, not an empty scaffold. Its existing behavior includes:

| Area | Current implementation | Boundary to keep in mind |
| --- | --- | --- |
| Workspaces and agents | Manifest-based configuration, a CLI, and an Orleans-backed host/runtime. | Agent actor keys remain `{workspaceId}/{agentName}`. Portable identity is not implemented by moving files. |
| Tool execution | `ToolActor` separates connection permission from `tool:<name>:invoke:<operation>` grants, normalizes adapter selectors, and rechecks authority before dispatch. Agent tool availability alone does not mint execution rights. | This is operation selection within the current workspace/tool model, not generalized resource/account constraints or installation revisions. |
| Invocation recording | The host requires an on-disk SQLite journal. Intent, authorization evidence and a distinct attempt are committed before dispatch; recording failure blocks the call. Stable IDs prevent repeat dispatch against that journal, and owner-scoped outcome lookup works after Host restart. | Single-host/preserved-file scope, metadata rather than response-body storage, no automatic unknown-outcome recovery or cluster-wide guarantee. |
| Stored proposals | A separate table in the same journal transaction retains immutable original input. Authorized UUID routes retrieve, review, decide and resume without a client body file. | New retained inputs only; historical missing bodies are not invented. Proposal storage contains confidential plaintext and needs protected retention/backups. UUID is not authority. |
| Durable approval | Configured filesystem operations wait for an independently authorized, exact-plan decision. Approval is consumed atomically with attempt admission; current execution authority is still required. | Reviewed HTTP decisions are separately opt-in. Filesystem is the first target-binding adapter; unsupported required targets fail closed. |
| Governed HTTP entry | Opt-in routes submit invocations and query outcomes/approval status using already signed capability tokens. UUID proposal/resume routes preserve those checks. | Global API auth remains separate. The explicit protected operator profile provides controlled onboarding, not anonymous self-service. |
| Verified approval review | An independent reviewer can retrieve a stored proposal by UUID for exact-plan verification. The existing read-only Dashboard at `/approvals/review` still uses explicit original-input verification. | The screen has no approve/reject/execute action. A snapshot is not an approval receipt. No reviewer-side secret resolution; unsupported or changed effective inputs fail closed. |
| Operator decisions | A Python terminal example fetches a verified proposal by UUID, displays complete escaped data and requires explicit digest-bound confirmation. The decision endpoint revalidates content before recording approve/reject without executing. | Requires administrator-provisioned connections and credentials. The Dashboard remains read-only; browser decision actions and human login are not implemented. |
| Connectors | MCP over stdio and HTTP/SSE, CLI, filesystem, OpenAPI, Direct HTTP, and Dapr extension projects. | An outbound MCP connector is not an inbound MCP governance server. Protocol coverage and security controls differ by adapter. |
| Tool plugin installations | Workspace-scoped Dapr and loopback HTTP MCP installations retain configured identity and enabled state across Host restarts when actor storage is durable. The MCP path pins one operation contract, routes calls through exact Agent grants, and requires [operator capabilities for installation and lifecycle changes](docs/implementation/2026-09-26-mcp-echo-installation.md). | The external MCP process is deployer-managed. This is not the general Tenant-scoped installation and resource model. |
| Authentication and credentials | Signed capability tokens, revocation handling, secret-protection code, and API authentication that rejects broken enabled configurations. | API auth defaults to `none`. The built-in bearer mode is a shared token, not a complete OAuth/OIDC or tenant identity system. |
| Verification | Automated tests, dependency auditing, CI, and release-source/package validation. | Passing checks does not establish complete tenant isolation, generalized provider approval coverage, or a sandbox for untrusted code. |

Relevant implementation: [tool dispatch](src/Invocations/InvokeTool/ToolActor.cs), [token service](src/Authority/Tokens/CapabilityTokenService.cs), [API authentication](hosts/Weave.Host/Security/ApiAuthOptions.cs), and [delivery records](docs/implementation/).

**Grant migration:** bare `tool:<name>` grants no longer authorize execution. Configure explicit `AgentDefinition.Capabilities`; for example, `tool:files:invoke:read_file` does not grant `write_file`. CLI execution requires `exec` authority, not a caller-supplied read-only label. Read the [operation-authority migration notes](docs/implementation/2026-09-19-exact-tool-operations.md) before upgrading existing workspaces or custom connectors.

**Invocation recording:** the host defaults to `~/.weave/invocations.db`; configure `Weave:Invocations:DatabasePath` for a persistent local path. There is no production in-memory fallback. Retain a caller-generated `InvocationId` before sending when response-loss recovery matters: omitted IDs create new logical calls. `IToolActor.GetInvocationAsync` requires separate `invocation:read` authority and the original workspace, tool and token subject. Queries and duplicate submissions return outcome metadata, not the original response body. Unknown outcomes must not be retried under a fresh ID. Read the [journal scope, query and migration notes](docs/implementation/2026-09-19-durable-invocations.md); separate silo-local files do not provide distributed deduplication.

**UUID proposals:** new requests persist their original input separately from audit metadata. Owners need both `invocation:read` and `invocation:proposal:read` to retrieve the body; reviewers use the independent exact-operation review path. Current execution rights are rechecked on empty-body UUID resume. The [proposal storage and API guide](docs/implementation/2026-09-24-authorized-proposal-uuid.md) describes the additive schema upgrade, confidential-content retention, terminal/MCP migration and missing historical-body behavior. No generic file/resource catalogue, response-body cache or automatic backfill is implied.

**Durable approval:** configure `Weave:Invocations:ApprovalRequiredGrants` explicitly; its default is empty. The operator uses `IToolActor.GetApprovalAsync` and `DecideApprovalAsync`; approval does not itself dispatch or grant execution permission. An approver needs `approval:decide` and the exact tool-operation approval grant, and cannot approve its own request. The original caller resumes the same ID with current execution authority; UUID HTTP resume retrieves the input server-side. Read the [approval configuration, decision and restart guide](docs/implementation/2026-09-20-durable-approval.md) before using it. Keep the journal outside agent-readable or writable tool roots and protect its files; it is not encrypted or tamper-proof audit storage.

**HTTP and readable review:** see the [capability-authenticated ingress guide](docs/implementation/2026-09-20-governed-http-entry.md), [verified review contract](docs/implementation/2026-09-20-verified-approval-review.md) and current [UUID extension](docs/implementation/2026-09-24-authorized-proposal-uuid.md). Review matches the original request and current target before returning content; it neither approves nor executes. The [read-only Dashboard screen](docs/implementation/2026-09-20-operator-review-screen.md) displays the complete verified content, target, digest and expiry. Enable it explicitly with `Weave:Review:Enabled` and an administrator-selected `Weave:Review:BaseUrl`. That screen retains its explicit-input mode; new UUID terminal/API access does not require uploading a body. Historical absent bodies remain unavailable, and an old preview cannot override later changes or expiry. Human login, browser decision actions and real-browser acceptance remain separate work.

**Reviewed decisions:** the [terminal operator example](examples/governed-tools/README.md) provides complete escaped review and explicit approval/rejection through the separately enabled reviewed decision endpoints. Prefer `review.py --invocation-id <UUID> --tool files` with the configured URL/workspace; no copied JSON file is required for a stored proposal. Enable `Weave:Invocations:Http:DecisionsEnabled` in addition to the governed HTTP profile. A decision re-verifies the original request and target; it neither executes the tool nor grants execution authority. The existing Dashboard is not changed into a decision surface by enabling this endpoint.

The existing Agent Runtime, memory, skills, channels, and other features remain in the repository. They are not prerequisites for the new Governed Tools profile. The current executable host still composes the existing runtime; extracting an extension does not, by itself, deliver every proposed deployment profile.

## Try the current development build

Build from source to explore this branch rather than assuming a published CLI release contains the new architecture.

**Prerequisites:** Git, a stable .NET SDK version 10.0.201 or newer selected by [global.json](global.json), and Python 3.10+ available as `python3` for the repository checks and MCP subprocess tests. The SDK policy selects the highest compatible installed version; inspect `dotnet --version` before building.

NuGet lockfiles include SDK-provided packages. CI installs the SDK used to generate the committed locks; a different compatible SDK may update those files during restore.

From the repository root:

```bash
git clone https://github.com/KoalaFacts/Weave.git
cd Weave

dotnet restore Weave.slnx
dotnet build Weave.slnx --no-restore -c Release

# Inspect the current command surface without starting a server.
dotnet run --project hosts/Weave.Cli/Weave.Cli.csproj --no-build -c Release -- --help
```

Create a starter configuration with the existing CLI:

```bash
dotnet run --project hosts/Weave.Cli/Weave.Cli.csproj --no-build -c Release -- workspace new demo --preset starter
```

This creates `demo/workspace.json` and supporting folders, and registers the workspace in the local Weave configuration. It does **not** call a model, provision an external resource, configure approval requirements, or demonstrate the operator approval workflow. Review generated configuration before starting a workspace; model-backed chat needs the selected model provider's configuration and credentials.

To exercise the existing connectors without a model account:

```bash
dotnet test --project tests/Weave.Tools.Tests/Weave.Tools.Tests.csproj --no-build -c Release
```

That suite includes a round trip through the real MCP connector and the repository's [Python echo server](examples/echo-mcp/server.py). Some tests require platform facilities such as symlink creation; inspect skipped tests as well as failures. These connector tests are not evidence of the target end-to-end authorization and approval path.

To exercise the host-level journal, approval and HTTP review scenarios, including complete Host recreation against the same SQLite file:

```bash
dotnet test --project tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj --no-build -c Release
```

The [approval restart test](tests/Weave.Silo.Tests/Invocations/ApprovalHostRestartTests.cs) demonstrates waiting without writing, restarting, an independent decision, explicit resubmission, and duplicate protection through the actual Orleans actor interface. The [HTTP review tests](tests/Weave.Silo.Tests/Invocations/GovernedHttpApprovalReviewTests.cs) verify readable content before a separately authorized decision. The Dashboard has a read-only review screen, but these server/client/rendering tests do not replace browser end-to-end acceptance or implement self-service onboarding.

For an operator-facing example, follow [Review and decide a pending operation](examples/governed-tools/README.md). Its real-process tests exercise approve, reject and leave-unchanged against Kestrel with both global and capability authentication. The helper never requests tool execution; the original caller resumes separately after approval.

For the current manifest format, see the [Manifest Reference](docs/manifest-reference.md). Older examples may retain pre-refactor paths; use `hosts/Weave.Cli/Weave.Cli.csproj` and the current CLI help. [Release history](https://github.com/KoalaFacts/Weave/releases) describes published versions separately from this development branch.

**Local evaluation is not a production deployment.** Keep unauthenticated development endpoints private. Before exposing a host, configure authentication and transport protection, restrict credentials and network access, and assess the deployment's actual isolation boundaries. Exposing the new governed routes does not secure the host's unrelated administrative endpoints.

## The model we are building

**Identity, context, and authority are separate.** In the target model, an Agent belongs to a Tenant independently of a Workspace. A Workspace organizes work; a Room supplies the working context inside it. Entering a Room does not automatically grant access to its resources.

| Concept | Question it answers |
| --- | --- |
| Agent / subject | Who is acting? Subjects can also be humans, services, or plugin installations. |
| Tenant, Workspace, Room | Within which security partition and working context? |
| Authority | Which exact operation is permitted, with what conditions and delegation? |
| Resource and binding | Which concrete asset is involved, and where is it available? |
| Plugin installation | Which configured provider instance will execute the operation? |
| Credential reference | Which approved authentication material may that executor use? |
| Invocation, attempt, approval, audit | What was requested, authorized, attempted, and actually confirmed? |

A mailbox, browser profile, repository, or cloud account is an example of a resource—not a promise that Weave currently provisions it. Resource binding makes an asset available in a context; it neither grants every operation nor transfers ownership. Deleting an agent or uninstalling an integration must not implicitly delete upstream assets.

### One governed invocation

The target execution path is:

```text
authenticate the caller and establish trusted context
  -> resolve the exact operation, installation, resource, and credential constraints
  -> normalize and freeze the plan
  -> authorize and obtain approval if required
  -> revalidate permissions, target, and approval
  -> persist the attempt and required local evidence
  -> execute through the selected adapter
  -> record the confirmed result or OutcomeUnknown
```

Preliminary access checks must precede disclosure of protected metadata. Approval binds to the actual plan: changing the account, target, operation revision, or meaningful inputs cannot silently reuse an old approval. Missing required authorization or recording prevents dispatch.

A timeout after dispatch may leave the outcome unknown. A local transaction cannot make an arbitrary external API execute exactly once; recovery needs provider-supported idempotency, status checks, or an explicit operator decision—not a blind replay.

HTTP, MCP, CLI, and UI entry points should share these semantics as they are implemented. Adding another entrance must not create a less-governed route.

### Existing integrations, not a replacement ecosystem

Weave's direction is to adapt existing MCP servers, command-line programs, and HTTP APIs before asking developers to build native plugins.

A **plugin definition** describes contributions and requested permissions. In the target model, a **plugin installation** identifies a Tenant-scoped configured instance with its own credentials, granted permissions, readiness, and lifecycle. Requested permissions are not granted permissions. The current Dapr and MCP paths implement narrower workspace-scoped installations.

| Installation ownership | Meaning |
| --- | --- |
| **Internal** | Weave manages the supported execution lifecycle. |
| **External** | Another system manages execution lifetime; Weave connects to it. |

Ownership is independent of protocol, language, and trust. A Weave-launched MCP process and an independently operated MCP server can use the same protocol but have different lifecycle ownership. MCP/CLI/HTTP/gRPC describe interfaces/adapters, not exclusive plugin kinds. OpenAPI describes an API contract. A process, container, or WASM runtime describes execution placement—not permission or guaranteed isolation. gRPC/WASM support is not implied by this taxonomy.

Operation identity must retain its provider/installation context and contract revision. Similarly named operations from different vendors are not automatically interchangeable.

## What comes next

Invocation ingress, UUID proposal access, verified review data, the read-only Dashboard and an explicit terminal decision flow are available. The separate Codex/human pilot still requires actual model, human-decision and isolation evidence. Follow the agreed scope in [PLAN.md](PLAN.md); do not treat this implementation as authorization for another workstream. Browser decision controls, generalized resource constraints and safe recovery from uncertain external results remain separate target capabilities.

A marketplace, hosted reasoning service, universal scheduler, or email/SMS infrastructure is not a prerequisite for the current governed tools profile.
