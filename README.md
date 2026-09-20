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
| Durable approval | Configured filesystem operations wait for an independently authorized, exact-plan decision. Approval is consumed atomically with attempt admission; the original request must be resubmitted with current authority. | Reviewed HTTP decisions are separately opt-in. Filesystem is the first target-binding adapter; unsupported required targets fail closed. |
| Governed HTTP entry | Opt-in routes submit invocations and query outcomes/approval status using already signed capability tokens. | Not public token issuance or self-service onboarding. Global API auth remains separate; restrict exposure to the governed routes. |
| Verified approval review | An independent reviewer submits the retained request; a read-only endpoint and Dashboard screen at `/approvals/review` display inputs and registered-target information only when they match the pending plan. | The screen verifies and displays; it has no approve/reject/execute action. A snapshot is not an approval receipt. No reviewer-side secret resolution; unsupported or changed effective inputs fail closed. |
| Operator decisions | A Python terminal example displays complete escaped review data and requires explicit digest-bound confirmation. The decision endpoint revalidates content before recording approve/reject without executing. | Requires administrator-provisioned connections and credentials. The Dashboard remains read-only; browser decision actions and human login are not implemented. |
| Connectors | MCP over stdio and HTTP/SSE, CLI, filesystem, OpenAPI, Direct HTTP, and Dapr extension projects. | An outbound MCP connector is not an inbound MCP governance server. Protocol coverage and security controls differ by adapter. |
| Authentication and credentials | Signed capability tokens, revocation handling, secret-protection code, and API authentication that rejects broken enabled configurations. | API auth defaults to `none`. The built-in bearer mode is a shared token, not a complete OAuth/OIDC or tenant identity system. |
| Verification | Automated tests, dependency auditing, CI, and release-source/package validation. | Passing checks does not establish complete tenant isolation, generalized provider approval coverage, or a sandbox for untrusted code. |

Relevant implementation: [tool dispatch](src/Invocations/InvokeTool/ToolActor.cs), [token service](src/Authority/Tokens/CapabilityTokenService.cs), [API authentication](hosts/Weave.Host/Security/ApiAuthOptions.cs), and [delivery records](docs/implementation/).

**Grant migration:** bare `tool:<name>` grants no longer authorize execution. Configure explicit `AgentDefinition.Capabilities`; for example, `tool:files:invoke:read_file` does not grant `write_file`. CLI execution requires `exec` authority, not a caller-supplied read-only label. Read the [operation-authority migration notes](docs/implementation/2026-09-19-exact-tool-operations.md) before upgrading existing workspaces or custom connectors.

**Invocation recording:** the host defaults to `~/.weave/invocations.db`; configure `Weave:Invocations:DatabasePath` for a persistent local path. There is no production in-memory fallback. Retain a caller-generated `InvocationId` before sending when response-loss recovery matters: omitted IDs create new logical calls. `IToolActor.GetInvocationAsync` requires separate `invocation:read` authority and the original workspace, tool and token subject. Queries and duplicate submissions return outcome metadata, not the original response body. Unknown outcomes must not be retried under a fresh ID. Read the [journal scope, query and migration notes](docs/implementation/2026-09-19-durable-invocations.md); separate silo-local files do not provide distributed deduplication.

**Durable approval:** configure `Weave:Invocations:ApprovalRequiredGrants` explicitly; its default is empty. The operator uses `IToolActor.GetApprovalAsync` and `DecideApprovalAsync`; approval does not itself dispatch or grant execution permission. An approver needs `approval:decide` and the exact tool-operation approval grant, and cannot approve its own request. The original caller resumes by resubmitting the same ID and request. Read the [approval configuration, decision and restart guide](docs/implementation/2026-09-20-durable-approval.md) before using it. Keep the journal outside agent-readable or writable tool roots and protect its files; it is not encrypted or tamper-proof audit storage.

**HTTP and readable review:** see the [capability-authenticated ingress guide](docs/implementation/2026-09-20-governed-http-entry.md) and [verified review contract](docs/implementation/2026-09-20-verified-approval-review.md). Review matches the retained original request and current target before returning content; it neither approves nor executes. The [read-only Dashboard screen](docs/implementation/2026-09-20-operator-review-screen.md) displays the complete verified content, target, digest and expiry. Enable it explicitly with `Weave:Review:Enabled` and an administrator-selected `Weave:Review:BaseUrl`. The journal does not supply a lost request body, and an old preview cannot override later changes or expiry. Human login, browser decision actions and real-browser acceptance remain separate work.

**Reviewed decisions:** the [terminal operator example](examples/governed-tools/README.md) provides complete escaped review and explicit approval/rejection through the separately enabled [reviewed decision endpoint](docs/implementation/2026-09-20-reviewed-approval-decisions.md). Enable `Weave:Invocations:Http:DecisionsEnabled` in addition to the governed HTTP profile. A decision re-verifies the original request and target; it neither executes the tool nor grants execution authority. The existing Dashboard is not changed into a decision surface by enabling this endpoint.

The existing Agent Runtime, memory, skills, channels, and other features remain in the repository. They are not prerequisites for the new Governed Tools profile. The current executable host still composes the existing runtime; extracting an extension does not, by itself, deliver every proposed deployment profile.

## Try the current development build

Build from source to explore this branch rather than assuming a published CLI release contains the new architecture.

**Prerequisites:** Git, the .NET SDK selected by [global.json](global.json), and Python 3.10+ available as `python3` for the repository checks and MCP subprocess tests. The SDK policy permits patch roll-forward; inspect `dotnet --version` rather than assuming an exact installed patch.

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

A **plugin definition** describes contributions and requested permissions. A **plugin installation** is a configured instance with its own credentials, granted permissions, readiness, and lifecycle. Requested permissions are not granted permissions.

| Installation ownership | Meaning |
| --- | --- |
| **Internal** | Weave manages the supported execution lifecycle. |
| **External** | Another system manages the execution lifetime; Weave connects to it. |

Ownership is independent of protocol, language, and trust. A Weave-launched MCP process and an independently operated MCP server can use the same protocol but have different lifecycle ownership. MCP/CLI/HTTP/gRPC describe interfaces; OpenAPI describes an API contract. A process, container, or WASM runtime describes execution placement—not permission or guaranteed isolation. gRPC/WASM support is not implied by this taxonomy.

Operation identity must retain its provider/installation context and contract revision. Similarly named operations from different vendors are not automatically interchangeable.

## What comes next

Invocation ingress, verified review data, the read-only Dashboard and an explicit terminal decision flow are available. The next user-facing milestone is real-browser acceptance and connecting browser approve/reject controls to the existing reviewed decision endpoint. It must not turn an unexplained digest, stale preview or self-issued identity into approval. Generalized target/resource constraints and safe recovery from uncertain results remain explicit work.

Then bring existing MCP/CLI/HTTP operations through the same path, add imported resource bindings and constrained credentials, and expand provisioning/reconciliation only for concrete workflows. A marketplace, hosted reasoning service, universal scheduler, or email/SMS infrastructure is not a prerequisite.

The detailed semantics and acceptance cases live in [ARCHITECTURE.md](ARCHITECTURE.md). Implementation records describe what each increment actually delivered; a roadmap entry is not an API contract.

## Architecture for contributors

**Vertical Slice + Composition.** Features own their contracts, behavior, and state. A slice completes a use case; a host selects implementations. There is no compulsory Core/Application/Infrastructure hierarchy or matching stack inside every feature.

```text
src/Weave.csproj   Product library; feature folders are directly under src/
src/Agents/       Identity-related behavior retained from the current runtime
src/Authority/    Grants and authorization
src/Invocations/  Tool execution, durable approval/admission and verified review
src/Plugins/      Plugin/discovery behavior
src/Resources/    Existing identifiers; generalized resource lifecycle is a target
hosts/            Executable hosts and their composition
extensions/       Opt-in protocol, runtime, and persistence implementations
tests/            .NET test projects
tools/            Source generators
scripts/          Repository checks and developer/release tooling
```

This is a map, not the full folder inventory. Add features when behavior needs them, not empty directories for roadmap nouns. Orleans is used by the current host; the target keeps it replaceable at appropriate runtime boundaries and out of external contracts. The reference application does not require a microservice per feature.

## Security boundaries

Weave can govern only the execution and credential paths it actually controls. An agent holding independent upstream credentials and unrestricted network access can bypass it.

In-process plugins are trusted code; an interface or assembly loader is not a sandbox. Separate processes or containers require deliberate filesystem, network, process, and credential restrictions. Leak scanning and redaction reduce exposure but do not guarantee prevention of prompt injection or exfiltration.

A policy document, test badge, or approved architecture is not proof that those boundaries exist in a deployment. Review the [security remediation record](docs/implementation/2026-09-19-security-remediation.md) and [release-chain record](docs/implementation/2026-09-19-release-chain-security.md) for the changes and limits of those increments.

## Contributing and documentation

Read [AGENTS.md](AGENTS.md) for the shared development rules, verification commands, and change-safety requirements. It is the single repository instruction source for coding assistants; `CLAUDE.md` only imports it. Human contributors use the same build and review requirements.

| Document | Purpose |
| --- | --- |
| [Architecture](ARCHITECTURE.md) | Target design, ownership, and security semantics. |
| [Shared contributor instructions](AGENTS.md) | How to make and verify changes in this repository. |
| [Implementation records](docs/implementation/) | Delivered increments, evidence, and known limits. |
| [Manifest reference](docs/manifest-reference.md) | Existing workspace configuration, not the proposed control-plane API. |

For a contribution or issue, identify the concrete operation or workflow, the expected outcome, and the failure case it needs to handle. Small, testable vertical slices are more useful than speculative infrastructure or a broad provider catalogue.

## License

Dual-licensed under your choice of the [MIT License](LICENSE-MIT) or [GNU Affero General Public License v3.0](LICENSE-AGPL).

SPDX: `MIT OR AGPL-3.0-or-later`.
