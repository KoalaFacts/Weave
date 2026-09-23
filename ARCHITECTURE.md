# Weave Architecture

> **Baseline:** Revision 2.1 — Vertical Slices, Explicit Composition, Flat Source Layout.
> **Product:** Agent Control Plane. **Reference language:** C#/.NET. **Plugin ecosystem:** language-neutral.
> **Implementation:** The source migration is the first structural increment, not completion of this target. See [implementation record](docs/implementation/2026-09-19-flat-source-foundation.md). Existing actor keys, tool-level permissions and runtime behavior are deliberately retained until their own tested replacements are implemented.

## 1. Architectural decision

**Features own behavior. Slices complete use cases. Components provide replaceable behavior. Hosts compose the product. Plugins extend it.**

There is no compulsory Core -> Application -> Kernel -> Infrastructure stack. A function does not need a controller/service/repository chain or a base-handler hierarchy to be valid. Composition and independent responsibility—not the number of layers—are the organizing principles.

Weave first governs existing agents' access to tools. An agent does not have to adopt the Weave reasoning runtime. The enduring responsibility is to represent intent, govern authority, bind resources, reconcile long-lived state and record accountable actions.

## 2. Source and project boundaries

```text
src/
  Weave.csproj
  Agents/
  Workspaces/
  Authority/
  Invocations/
  Plugins/
  Resources/
  Credentials/
  Audit/
  Composition/
hosts/
  Weave.Host/
  Weave.Cli/
  Weave.Dashboard/
  Weave.AppHost/
  Weave.ServiceDefaults/
extensions/
  Weave.AgentRuntime/
  Weave.Mcp/
  Weave.CliTools/
  Weave.OpenApi/
  Weave.DirectHttp/
  Weave.FileSystem/
  Weave.Dapr/
  ...other opt-in implementations...
tools/
  Weave.SourceGen/
tests/
  ...feature and compatibility test suites...
protocol/
  ...versioned schemas and cross-language fixtures as implemented...
```

Feature folders MUST be direct children of `src/`: no product-name or `Features/` wrapper. Additional real features such as Rooms, Channels, Memory and Skills are not technical layers. Do not create empty feature trees before a use case exists.

The product project is `src/Weave.csproj`; its assembly is `Weave.Product`, avoiding a case-insensitive collision with the existing CLI assembly `weave`. Its namespace root is `Weave`. Physical directories need not mirror the project name.

The current structural migration retains existing namespaces to avoid combining a package/host refactor with an unreviewed protocol or persistence migration. Those names are not a reason to recreate Shared/Core layers.

A feature owns requests, results, behavior, state, data access and endpoint adapters. Related use cases may share a state model. Example target:

```text
Invocations/
  InvokeCapability/
    InvokeCapability.cs
    InvokeCapabilityHandler.cs
    InvocationPlan.cs
  DecideApproval/
  ResumeInvocation/
  GetInvocation/
  Invocation.cs
  InvocationAttempt.cs
  ICapabilityExecutor.cs
```

Do not create project-wide Models, Services, Repositories, Controllers or Actors baskets. Do not reproduce Domain/Application/Infrastructure/Presentation inside every feature. A simple query can use its data access directly. Extract components for actual variability, reuse, independent testing or lifecycle boundaries.

A module is not necessarily an assembly or service. A slice need not become a plugin. Separate projects are justified by optional third-party dependencies, independent packaging, deployment or isolation—not by every architectural noun.

## 3. Ownership and contracts

| Feature | Owns |
|---|---|
| Agents | Stable subject identity, definition, status and references |
| Workspaces | Administrative grouping and configuration |
| Rooms | Working context, participation and contextual availability |
| Authority | Grants, scopes, revocation, delegation and access decisions |
| Invocations | Operation plans, approvals, attempts, outcomes and recovery |
| Plugins | Definitions, installations, contribution discovery and availability |
| Resources | Import, claim, class, binding, observation, convergence and release |
| Credentials | Credential references, controlled use and revocation metadata |
| Audit | Authorized search, projections, retention and export |

Public contracts belong to the feature that defines their semantics. A use case's input/output should remain near its implementation. Cross-feature callers use explicit public operations and immutable snapshots, not neighboring handlers, mutable entities, storage internals or a service locator.

Do not collect every type in a global Contracts/Core package. A lightweight SDK can be extracted for genuine external consumers without changing feature ownership. One semantic identity has one authoritative internal definition; explicit wire representations may differ.

Local interfaces can accept CancellationToken. Network messages cannot contain CancellationToken, HttpContext, IServiceProvider, Orleans runtime objects, ORM entities or delegates. External protocols specify identifiers, timestamps, numeric precision, enum handling, deadlines, cancellation, errors, size/depth limits and version compatibility. Mutable payloads must be copied or otherwise owned; a record does not make a mutable child immutable.

## 4. Explicit composition

The Host chooses modules, adapters, persistence, authentication and operational configuration. It implements no business decisions. Constructor/method dependencies are explicit. Dynamic service lookup is limited to actual dispatch/composition machinery.

The initial profile is Governed Tools. A reasoning runtime, memory, skills and model integrations are optional consumers of the same governed operation API.

Startup validates required services, unambiguous executor bindings, compatible contracts and durable recording. Missing Authority or a mandatory journal makes governed execution not ready. Do not use last-registration-wins for safety-critical collaborators.

Provider selection uses installation/provider identity and operation/resource contract, not DI ordering or only a resource kind. Keep the concrete startup graph acyclic: resource binding readers do not themselves initialize invocation orchestration.

Built-in features are composed at startup. Connections/installation instances can change at runtime. Replacing arbitrary live authorization code, persistence or managed DLLs is not an initial requirement.

## 5. Identity and authority

Tenant is the hard security partition. Agents and Workspaces are independent entities within it. A Room belongs to a Workspace and provides execution context. Room access does not follow automatically from a matching Agent name or previous Room membership.

The target distinguishes stable Agent identity from worker, activation, default Workspace and current Room. Existing workspace-bound actor keys require a separate explicit migration; moving folders does not implement this change.

Ingress authenticates the caller and establishes trusted subject/Tenant context. Client-supplied Agent, Room and resource identifiers are requests to validate, not proof of authority. Lookups, credentials and provider routing remain Tenant-scoped. Background work restores context from durable records.

Subject kinds include Human, Agent, Plugin installation and Service. Administrative ownership, sponsorship, resource binding and permission are separate relationships. Deleting an Agent does not implicitly delete external assets.

Authority evaluates exact operations and applicable grants, expiry, revocation, policy, resource/installation/credential constraints and delegation. It must not merge grants in a way that drops conditions. Explicit denial, unknown decisions and unfulfilled mandatory obligations prevent execution.

Administrative actions—granting permissions, approving, binding resources, installing plugins, registering credentials and exporting audit—also need their own authorization. Initial administrator bootstrap is an explicit one-time deployment operation, not a permanent unauthenticated route.

## 6. Capabilities and tool semantics

Discovery is not authorization. Adapted operation identity includes installation/Tenant context, upstream operation identity and contract revision. A display alias is not a global security key.

Preserve server-scoped MCP tool identity and schemas. Tool annotations and descriptions are untrusted hints, not authorization evidence. New tools are ungranted. Changes to schemas or behavior-bearing descriptors require re-evaluation.

Introduce portable semantic contracts such as `mail.send` only with precise inputs, outputs, resource requirements, constraints and conformance tests. Similarly named vendor operations are not presumed interchangeable. Unsupported requirements must not be silently dropped.

## 7. Governed invocation

`Invocations/InvokeCapability` owns one complete use case, not a technical-layer pipeline:

```text
authenticated context
  -> resolve exact operation, resource and installation
  -> normalize and freeze the operation plan
  -> evaluate current authority and policy
  -> obtain durable, specific approval when required
  -> revalidate authority, target and plan
  -> persist execution attempt and required local evidence
  -> execute through the selected adapter
  -> persist confirmed or unknown outcome
```

Preliminary access checks happen before protected metadata is disclosed. Final authorization uses the resolved target and parameters, not a tool name followed by hidden default-account selection.

The plan binds Invocation ID, subject/delegation, Tenant/Room context, exact operation revision, installation, resource/binding revisions, normalized inputs and constrained credential selection. Its digest uses a documented canonical representation. Meaningful input/target changes happen before approval. Raw secrets never enter the approval payload.

Required resource omission or ambiguity is rejected. Resource-free operations declare that fact. Executors must not substitute a different account, resource, installation or more privileged credential after approval.

Invocation and Attempt are separate durable identities. States distinguish Received, AwaitingApproval, Ready, Executing, Succeeded, Failed, Denied, Expired, Cancelled and OutcomeUnknown. Only explicitly tested transitions are valid.

Before dispatch, atomically persist the attempt and required local audit evidence. Failure to record prevents dispatch. A local transaction cannot guarantee exactly-once execution of an arbitrary external API. Idempotency keys, external status queries and explicit recovery provide the available guarantees.

A timeout, lost response or cancellation after dispatch may mean OutcomeUnknown. It is not proof of no side effect and is not permission for blind replay. Disconnecting the caller does not erase an accepted operation. Completion recording uses a bounded service-owned context when appropriate.

## 8. Approval

Approval state initially belongs to Invocations. Notification transports do not own decisions.

Approval binds the exact plan, subject/context, target, operation revision, policy evidence and expiry. Pending, Approved, Rejected, Expired and Cancelled are distinct. Rejected/expired/cancelled approval is never treated as indefinitely pending.

Recheck current grants, subject/Room access, binding/resource state, installation readiness and approval validity before dispatch. A changed plan cannot reuse approval unconditionally. Durably coordinate approval consumption and attempt claiming across workers.

Do not hold a request, thread or database transaction open while a human decides. Return a durable ID and resume through explicit operations. Revocation does not undo an already started external effect.

## 9. Plugins

A PluginDefinition/package describes versioned contributions and requested permissions. A PluginInstallation identifies a Tenant-scoped configured instance with its own credentials, actual grants, readiness and lifecycle.

| Ownership | Meaning |
|---|---|
| Internal | Weave manages supported lifecycle operations through a runtime class |
| External | Another system manages execution lifetime; Weave connects, authenticates, discovers and invokes |

Ownership is an installation attribute. It is independent of language, protocol, container/process placement and trust. MCP/CLI/HTTP/gRPC are interfaces/adapters, not exclusive plugin kinds. OpenAPI is an interface description. Open extension keys are namespaced/versioned, not a central enum for every provider.

Requested permissions are not granted permissions. Deployment policy assigns trust; an author's manifest cannot self-grant it. A tools-only plugin need not implement resource reconciliation. Unsupported protocol families are explicit.

Disable first prevents new dispatch, then drains/cancels according to declared semantics. Live handlers can be disposed, but resource records, schemas, installed revisions, history and approval evidence remain. Uninstall does not delete upstream assets. Upgrade cannot silently redirect an approved plan to a changed implementation.

## 10. Resources and reconciliation

Resources owns generic relationships and lifecycle. Specific mailbox, telephone, browser, account or future resource semantics belong to extensions.

Import/reference/bind is available before provisioning. Later ResourceClaim expresses demand, ResourceClass maps to approved provider policy, Resource records state, and ResourceBinding makes it available in a context without granting every operation or transferring legal ownership.

Record Tenant, kind/schema version, installation/provider identity, desired/observed generation, concurrency version, conditions and retention/deletion policy. Payloads remain bounded and schema-validated. Class changes do not silently migrate existing assets.

Reconciliation is not replaying a non-idempotent action. It needs exact routing, persisted state, version checks, ownership/leases, bounded retries, requeue persistence, failure classification and explicit release semantics. Provider idempotency and authoritative observation are used where available. A lease alone does not fence an upstream API.

Controllers propose bounded changes before privileged application; they use governed dispatch and recording through public feature contracts, not unrestricted credentials or old human tokens. Missing consent or unsupported behavior produces Blocked/Unsupported, not fictitious Ready.

## 11. Credentials and actual security boundary

CredentialReference identifies approved material, not an ordinary string exposed to models. Prefer brokered or narrow short-lived use. When a subprocess or SDK must receive a secret, that adapter/environment is part of the trusted execution path.

Isolation is by Tenant and installation. Audit stores references and relevant versions/scopes, not secret values. A matching tool/package name never permits another installation's credentials.

Weave governs only entry points and credential/runtime/network boundaries it actually controls. An agent with independent upstream credentials and network access can bypass that boundary.

In-process .NET code is trusted code; AssemblyLoadContext is not a security sandbox. Untrusted plugins require properly restricted filesystem/process/network/credential access, not merely a separate process or a signature. Tool output and descriptions cannot grant authority or approve actions. Redaction is not a guarantee of eliminating prompt injection/exfiltration.

## 12. Integrations, state and deployment

Inbound MCP/HTTP accepts external agents; outbound MCP executes approved plans against configured servers. Both directions are required for a governance product. Existing MCP servers speak MCP; they do not need fictitious Weave methods.

CLI operations declare executable, argument mapping, environment, working-directory and resource limits. Prefer argument arrays; shell access is a separate privileged operation. Drain stdout/stderr concurrently, bound capture, redact outputs and handle timeout/cleanup. Exit code alone need not prove business success.

Calls obtain immediate decisions/results; events describe committed facts. Consumers handle duplicates. No feature writes another feature's state directly. A shared database is acceptable; one database per slice is not required.

Keep strong invariants with their state owner or an explicit local transaction. Use an outbox/equivalent durable delivery for audit projections and notifications. No transaction spans a human decision or arbitrary provider call. Compensation must be supported explicitly.

C#/.NET and ASP.NET Core are the reference implementation. Orleans is optional where it serves a real feature and stays out of wire contracts. Initial deployment is a composable application, not mandatory microservices. Multi-node claims need concurrency/recovery tests.

Native AOT is tested per host. It cannot simultaneously promise arbitrary dynamic managed-assembly loading. Static built-ins and external workers are separate implementation paths. Rust/WASM infrastructure is not an initial requirement.

## 13. Audit and verification

Audit is not debug logging or a substitute for the Invocation journal. Preserve attribution, exact plan/installation, authority evidence, approval, attempt, relevant credential reference and confirmed/unknown result. Export can be asynchronous; a missing mandatory local journal blocks new privileged work.

Logs/metrics/traces use correlation IDs and bounded redacted payloads. Audit queries/exports and retention are themselves authorized. Optional provider health failure need not disable unrelated providers; missing mandatory governance makes a profile not ready.

Tests follow features and actual behavior. Preserve the repository's xunit.v3/Shouldly/NSubstitute toolchain. Test real persistence/concurrency guarantees; mocks alone do not prove durability. Static namespace/dependency checks supplement visibility inside a shared assembly.

Release gates include: read/write separation; revoked/expired grants; Room isolation; unknown policy decisions; ambiguous resources; rejected/expired approval; changed-plan rejection; concurrent resume; journal failure; upstream success with lost response; cancellation uncertainty; installation isolation; tool-schema drift; plugin disable without asset deletion; identical ingress semantics; immutable plans; recoverable audit export; and non-.NET protocol conformance. Resource gates also include stale/concurrent observation, provider routing, retention and missing-consent behavior.

## 14. Adoption and rewrite discipline

First prove one governed operation using a deterministic executor, then existing MCP/CLI tools with real authorized upstream tests. Start with imported resources; add provisioning and reconciliation only after that path works.

Do not require a public marketplace, broad provider catalogue, hosted reasoning, payment/email/SMS infrastructure, model router, universal scheduler or visual workflow builder before first use. Native plugins follow real demand.

Rewriting is permitted. For every replaced behavior, record preservation, deliberate change or removal. Never keep an ungoverned alternate write path, silently reset production state, or infer migration safety from compiling source. A structural PR is not authority to deploy or merge to main.

## References

Primary references for external constraints (architecture choices remain Weave decisions):

- Jimmy Bogard, Vertical Slice Architecture: https://www.jimmybogard.com/vertical-slice-architecture/
- .NET DI guidance: https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/guidelines
- AssemblyLoadContext security limitation: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext
- Native AOT limitations: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- MCP tool contracts: https://modelcontextprotocol.io/specification/latest/server/tools
- Kubernetes controllers: https://kubernetes.io/docs/concepts/architecture/controller/

> Authority remains explicit. Execution remains accountable. Directory structure supports those properties; it does not create them by itself.
