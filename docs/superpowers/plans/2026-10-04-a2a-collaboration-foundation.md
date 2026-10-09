# A2A Collaboration Foundation Implementation Plan

> **Superseded direction (2026-10-04):** The user replaced this governed-note collaboration scope with direct agent access to an encrypted, expiring Cloudflare inbox/outbox carrying selected A2A payloads. See the [replacement mailbox specification](../specs/2026-10-04-encrypted-agent-mailbox-design.md). This historical document is retained for context; do not execute this plan or treat its earlier approval as approval of the replacement implementation.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove one approved note exchange between two synthetic owners' agents using A2A 1.0 and 0.3, with scoped durable tasks and existing governed invocation semantics.

**Architecture:** Collaboration owns task admission, receipts, queries, recipient continuation, and artifact projection. Authority owns the peer agreement decision; Invocations enforces a mandatory shared pre-dispatch gate across every ingress. An opt-in HTTP+JSON extension translates both wire revisions without introducing a second executor or approval system.

**Tech Stack:** C#/.NET 10, ASP.NET Core, existing Microsoft.Data.Sqlite provider, System.Text.Json source generation, xunit.v3, Shouldly, NSubstitute, Python standard library. No A2A SDK or new third-party package is required.

**Spec:** [Approved A2A foundation design](../specs/2026-10-03-a2a-collaboration-foundation-design.md), approved against PR #181 at `2ad02932b9c26d8f6e40f3b794b1cf8dc9b9769d`. The original product baseline was `b658a5f33741311ffc4e414f01ab14400cb3c569`; prerequisite status is reconciled with main `1e8e09f77f9d22d994e392d377ac03ab8a53a0eb` after [PR #182](https://github.com/KoalaFacts/Weave/pull/182) and [PR #183](https://github.com/KoalaFacts/Weave/pull/183).

**Status:** Proposed implementation plan for review and execution-method selection. No product implementation has started. Documentation integration does not approve the plan or its execution method.

## Global Constraints

- “Pin the source specifications to `v1.0.1` and `v0.3.0`.” Use wire `1.0`; retain the specified `0.3.0` field in the older AgentCard
- “Expose HTTP+JSON only.” 1.0 requires `configuration.returnImmediately: true`; 0.3 accepts absent/false `configuration.blocking`
- “No preview SDK dependency is required by the selected design.” Keep optional protocol code in `extensions/Weave.A2A/`
- “Request body: 64 KiB; note text: 16 KiB UTF-8; JSON depth: 16; exactly one supported application data part per skill message”
- “Task-owned transcript: initiating and continuation messages only, at most 16 retained messages per task; excess continuation requests are rejected before mutation”
- “Artifact total: 24 KiB per task; at most two artifacts, one approved note and one receipt; never return a local path or raw exception”
- “Per registered peer/recipient profile: at most 100 retained tasks and 8 active tasks.” Reject capacity overflow; retain evidence
- “The 1.0 default page is 50 and accepted page size is 1 through 100.” The 0.3 list is an array, bounded by retained-task quota
- “Loopback connection and request deadlines: 5 and 30 seconds respectively. No automatic send retry.” Poll no faster than once a second
- “The initial profile accepts only configured literal loopback origins and disabled redirects, proxies, and cookies.” No real peers, production credentials, LLM accounts, or deployment; repository integration follows the user's current authorization and verified checks
- “A unique key combines stable authenticated source identity and message ID within the receiving authority.” Destination, installation, plan, and protocol revision belong in the compared digest
- “The reserved collaboration envelope requires its gate.” Disabled A2A routes never disable protection of retained collaboration invocations
- Preserve current actor keys, append-only Orleans field IDs, licensing, warnings-as-errors, audit, exact grants, approval independence, durable admission, and unknown-outcome rules
- Use `TimeProvider`; source-generated JSON; owned immutable snapshots; no hidden service locators, automatic replay, test weakening, or production in-memory storage fallback
- PR #182 landed Codex-launch executable/workspace overlap, nested preflight-error, and null-path repairs at `b99f1fc7362b3c6f4af5692261441cf04e73f60e`. PR #183 added initial-Host redirected-path validation at `1e8e09f77f9d22d994e392d377ac03ab8a53a0eb`, with passing post-merge Linux/Windows CI. Packaged-agent acceptance still requires A21 at the actual launch-tested revision; same-user validation is not OS/cross-user isolation or new live human acceptance. This plan does not edit the Local CLI files

## Review Focus

1. Duplicate or case-variant JSON properties must not let validation and execution read different owners/actions/content; pin rejection in Task 5
2. A revoked agreement must stay revoked across store/Host restart, and a newer agreement revision cannot revive an old approval; pin persistence in Task 2 and dispatch denial in Task 3
3. Reusing a message ID through another protocol revision or recipient installation must conflict instead of opening another task; pin Task 2
4. Removing the collaboration marker before the first tool proposal exists must not turn a bound UUID into an ordinary invocation; pin lookup and denial in Task 3
5. A journal-confirmed write followed by lost task finalization must produce the same artifact after restart without another write; pin deterministic recovery in Task 4

---

## Execution and branch discipline

Recommend **subagent-driven execution**, with one implementer and fresh reviewer per task, then a whole-branch review. The shared dispatch gate and durable recovery are security boundaries; independent rejection of a task is worth the extra review. Do not run simultaneous writers against the same files. Tasks 1–4 are sequential; after Task 4, the protocol work in Task 5 and read-only fixture preparation for Task 7 can proceed independently. Task 6 requires Task 5; Task 7 requires all prior tasks.

At execution time, read `AGENTS.md`, `ARCHITECTURE.md`, the approved spec, and relevant checkout skills. Use an isolated `feat/a2a-collaboration-foundation` branch/worktree based on current main containing the reviewed documentation and PRs #182/#183's repairs, following the worktree skill. Verify the exact base, the initial-Host path correction, and retained repair coverage before any packaged launch; do not merge or cherry-pick PR #180 again. Keep the specification and plan with the implementation branch.

Desktop is currently offline and no saved coding environment is available. The documented engineering fallback permits the cloud workspace. Before implementation, check its SDK/toolchain and authorized repository access. Current document authoring did not run .NET. If the required .NET 10 stable SDK, package access, or test prerequisites are unavailable, report the specific blocker; do not claim tests passed or weaken the gates. Installing an official toolchain follows the existing confirmation requirements.

For every task: add a focused failing regression, record its intended failure, implement, run the targeted module, self-review the diff, then make the listed commit. Constructor changes require updating actual direct callers and test factories discovered with `rg 'new ToolActor|new InvocationExecution'`; no compatibility overload may bypass the new guard.

## File and contract map

New product namespaces are `Weave.Collaboration` and `Weave.Authority.Collaboration`; retained Invocations namespaces stay unchanged. Each listed record/interface has its own named `.cs` file in its semantic directory. New IDs use existing branded-ID conventions. All new records own collection values using immutable arrays/maps; no borrowed mutable dictionary crosses a public operation.

The following shared records are the handoff contract, introduced by the task indicated:

| Task | Type and complete semantic fields |
| --- | --- |
| 1 | `PeerIdentity`: receiving authority ID, source owner ID, source agent ID, authenticated workspace ID, authenticated subject |
| 1 | `PeerAgreement`: agreement ID, source identity, recipient owner/agent/workspace, installation ID, tool name, skill revision, target revision, registered adapter target digest, wire revision, allowed input/output modes, revision, expiry, enabled |
| 1 | `PeerScope`: authenticated peer, recipient registration ID, operation (`Send`, `Read`, `Cancel`), wire revision |
| 1 | `PeerPolicyDecision`: allowed flag, exact agreement snapshot or null, bounded error code or null |
| 1 | `NoteCommand`: `Propose(text)` or `Resume(taskId, contextId?)`; invalid combinations are rejected |
| 1 | `FrozenNotePlan`: task/context IDs, invocation UUID, complete agreement/participant snapshot, exact note, server-derived relative path, canonical envelope, input digest |
| 1 | `CollaborationFailure`: bounded code, safe explanation, retry classification (`Never`, `QueryOnly`, `SameMessageExplicit`) |
| 1 | `CollaborationResult<T>`: exactly one value or failure; named `Success` and `Failure` factories |
| 2 | `TaskSnapshot`: IDs, immutable plan, internal state, version, status timestamp, bounded failure or null, scoped messages/artifacts, invocation outcome/attempt reference, continuation-permit revision and dispatch-claim attempt ID; no credential |
| 2 | `CollaborationTaskState`: Submitted, AwaitingApproval, Working, Completed, Rejected, Canceled, Failed, OutcomeUnknown; `CollaborationMessage`: ID, action, safe text, recorded timestamp; `CollaborationArtifact`: Id, Name, ContentType, Text (literal content) |
| 2 | `OutgoingReceipt`: source/message ID, exact normalized request bytes, chosen origin/recipient/agreement/wire revision, digest, optional learned task/context IDs |
| 2 | `TaskQuery`: optional context/status/updated-after, page size, opaque cursor, history limit, include-artifacts; `TaskPage`: tasks, next cursor, page size, scoped total |
| 3 | `InvocationGateInput`: workspace, subject, normalized invocation with owned parameters; `InvocationGateDecision`: allow/deny with bounded reason |

`FrozenNotePlan` and `TaskSnapshot` must not contain secret substitutions or capability tokens. A fresh capability is supplied explicitly to each recipient operation.

### Task 1: Peer authority and literal frozen note

**Files:** Create `src/Authority/Collaboration/{PeerIdentity,PeerAgreement,PeerScope,PeerPolicyDecision,PeerAgreementAuthority,IPeerAgreementStore}.cs`; create `src/Collaboration/{CollaborationTaskId,CollaborationContextId,CollaborationFailure,CollaborationResult,RetryClassification}.cs`; create `src/Collaboration/SubmitTask/{NoteCommand,FrozenNotePlan,ReviewedNotePlanner}.cs`; create tests `tests/Weave.Security.Tests/Collaboration/PeerAgreementAuthorityTests.cs` and `tests/Weave.Tools.Tests/Collaboration/ReviewedNotePlannerTests.cs`. Use the existing internal `src/Credentials/Scanning/SecretPlaceholderParser.cs` from the same product assembly.

**Interfaces:**
- `PeerAgreementAuthority.AuthorizeAsync(PeerScope scope, CancellationToken ct) -> Task<PeerPolicyDecision>`
- `PeerAgreementAuthority.WithCurrentAsync<T>(PeerScope scope, long expectedRevision, Func<PeerAgreement,T> localStoreAction, CancellationToken ct) -> Task<CollaborationResult<T>>`; serializes the bounded store action with agreement changes, never a provider call or human wait
- `PeerAgreementAuthority.RevokeAsync(string agreementId, long expectedRevision, CapabilityToken operatorToken, CancellationToken ct) -> Task<CollaborationResult<long>>`; requires `collaboration:agreement:manage` in the receiving workspace; no new HTTP administration endpoint
- `IPeerAgreementStore.Find(PeerScope scope, CancellationToken ct) -> PeerAgreement?`; `FindById(string agreementId, CancellationToken ct) -> PeerAgreement?`; `TryReplace(PeerAgreement value, long expectedRevision, CancellationToken ct) -> bool`
- `ReviewedNotePlanner.Create(PeerAgreement agreement, CollaborationTaskId taskId, CollaborationContextId contextId, InvocationId invocationId, string text) -> CollaborationResult<FrozenNotePlan>`

Peer operation grants are exactly `collaboration:<recipientRegistrationId>:send`, `collaboration:<recipientRegistrationId>:read`, and `collaboration:<recipientRegistrationId>:cancel`, checked in the receiving workspace. Registration IDs are server-configured route-safe identifiers; no caller-selected grant string is accepted. Planner input digests use the existing `InvocationFingerprint.ComputeInputDigest` over the fixed FileSystem operation, owned parameters, receiving workspace and original recipient subject.

- [ ] Add declarations and tests that fail closed until implemented. Assert the following named cases with exact outcomes:

```csharp
// AuthorizeAsync_WrongRecipient_DeniesWithoutAgreement
result.Allowed.ShouldBeFalse(); result.ErrorCode.ShouldBe("peer-forbidden");
// Create_16384Utf8Bytes_Accepts_16385Rejects
atLimit.IsSuccess.ShouldBeTrue(); overLimit.Error.Code.ShouldBe("note-too-large");
// Create_RecognizedSecretPlaceholder_RejectsEvenIfUnregistered
result.Error.Code.ShouldBe("literal-secret-placeholder");
// Create_ChangesOnlyPeerOrWireRevision_ChangesDigest
changed.InputDigest.ShouldNotBe(original.InputDigest);
```

Also test empty text, multibyte Unicode boundary, expired/disabled policy, forged role/owner values, and path derivation exactly `<invocation-uuid>.txt` with no caller path.
- [ ] Run `dotnet test --project tests/Weave.Security.Tests/Weave.Security.Tests.csproj -c Release` and the Tools module; verify intended authorization/planning assertions fail, not an unrelated environment failure
- [ ] Implement exact grant checks, immutable policy snapshots, injected time, literal-placeholder rejection, canonical stable envelope, and fixed skill `weave.reviewed-note.v1`. `WithCurrentAsync` uses one explicit coordinator shared with RevokeAsync; concurrent policy changes cannot race its local-store action
- [ ] Rerun both modules; require every new case and existing tests to pass
- [ ] Commit `feat: define scoped peer authority and reviewed note plans`

### Task 2: Durable task state, receipts, and scoped queries

**Files:** Create `src/Collaboration/Storage/{ICollaborationStore,TaskSnapshot,CollaborationTaskState,CollaborationMessage,CollaborationArtifact,OutgoingReceipt,TaskQuery,TaskPage,CollaborationStoreOptions}.cs`; create `extensions/Weave.Security.Sqlite/Collaboration/{SqliteCollaborationStore,CollaborationSchema,CollaborationStorageJsonContext,SqlitePeerAgreementStore}.cs`; create `tests/Weave.Silo.Tests/Collaboration/{CollaborationStoreTests,CollaborationStoreConcurrencyTests,CollaborationStoreRestartTests}.cs`. Keep the existing invocation schema untouched.

**Interfaces:** `ICollaborationStore` uses short synchronous SQLite operations with CancellationToken, matching the current provider. Expose:
- `Admit(PeerIdentity source, string messageId, FrozenNotePlan plan, CancellationToken ct) -> CollaborationResult<TaskSnapshot>`
- `Find(PeerScope scope, CollaborationTaskId id, CancellationToken ct) -> TaskSnapshot?`
- `FindBinding(string workspaceId, InvocationId id, CancellationToken ct) -> TaskSnapshot?` for trusted dispatch checks, independent of caller subject/marker
- `List(PeerScope scope, TaskQuery query, CancellationToken ct) -> CollaborationResult<TaskPage>`
- `TryPermitResume(CollaborationTaskId id, long expectedVersion, long agreementRevision, CancellationToken ct) -> bool`
- `TryClaimDispatch(CollaborationTaskId id, long expectedVersion, InvocationAttemptId attemptId, CancellationToken ct) -> bool`
- `TryCancel(PeerScope scope, CollaborationTaskId id, CancellationToken ct) -> CollaborationResult<TaskSnapshot>`
- `TryUpdate(TaskSnapshot next, long expectedVersion, CancellationToken ct) -> bool`
- `SaveOutgoing(OutgoingReceipt candidate, CancellationToken ct) -> CollaborationResult<OutgoingReceipt>`; `FindOutgoing(PeerIdentity source, string messageId, CancellationToken ct) -> OutgoingReceipt?`

`CollaborationStoreOptions.DatabasePath` is an explicit absolute on-disk path; `RequireExistingStorage` rejects missing/incompatible retained storage. Use the same configured protected database file as the invocation journal for additive collaboration/peer tables, but access them only through their feature contracts. The always-active guard cannot lose track of bindings when A2A routes are disabled.

- [ ] Add real-file tests for atomic source/message deduplication, conflicting destination/wire digests, 100 retained/8 active quotas, three-owner scoped list/counts, opaque principal/filter-bound cursors, 16-message limit, immutable snapshots, and cancellation-versus-claim compare-and-set

```csharp
// Admit_ConcurrentIdenticalMessage_ReturnsOneTaskAndInvocation
snapshots.Select(x => x.TaskId).Distinct().Count().ShouldBe(1);
// Admit_SameMessageDifferentInstallation_Conflicts
result.Error.Code.ShouldBe("message-id-conflict");
// Restart_AgreementRevokedAndReceiptRetained_DoesNotRestoreAuthority
reopenedAgreement.Enabled.ShouldBeFalse(); reopenedReceipt.Digest.ShouldBe(original.Digest);
```

- [ ] Run `dotnet test --project tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj -c Release`; require intended persistence/concurrency failures before implementation
- [ ] Implement versioned additive schema, WAL/full synchronous durability, uniqueness by `(receivingAuthorityId, sourceOwnerId, sourceAgentId, messageId)` rather than rotating token/subject evidence, typed short transactions, persisted peer revocation, scoped queries and cursor snapshots, and durable outgoing receipts. Empty/foreign cursors fail without cross-scope counts. TryUpdate may change only the state projection, bounded messages/artifacts, and permit/claim fields; it rejects any change to immutable participants, IDs, plan or digest. Add `TryUpdate_ChangesFrozenPlan_Rejects` with unchanged readback. No automatic evidence deletion or `:memory:` production fallback
- [ ] Run tests with two independent store instances and reopened files; assert no lost updates, preserved prior invocation tables, denied unknown/newer schema, and no state creation on invalid path
- [ ] Commit `feat: persist scoped collaboration tasks and outgoing receipts`

### Task 3: Enforce one gate in every invocation path

**Files:** Create `src/Invocations/{IInvocationDispatchGate,InvocationGateInput,InvocationGateDecision}.cs`; create `src/Collaboration/ExecuteTask/CollaborationInvocationGate.cs`; modify `src/Invocations/InvokeTool/ToolActor.cs`, `src/Invocations/InvokeTool/InvocationExecution.cs`, `hosts/Weave.Host/VirtualActors/Tools/ToolActorGrain.cs`, and `hosts/Weave.Host/Startup/SiloServiceRegistrar.cs`; create `tests/Weave.Tools.Tests/Collaboration/InvocationDispatchGateTests.cs`, `tests/Weave.Silo.Tests/Collaboration/GenericIngressGateTests.cs`, and `tests/Weave.Tools.Tests/Collaboration/TestInvocationDispatchGate.cs`. Update direct constructor call sites found by the execution-time search, retaining their behavior assertions.

**Interfaces:**
- `IInvocationDispatchGate.InspectAsync(InvocationGateInput input, CancellationToken ct) -> Task<InvocationGateDecision>` before secret substitution/proposal admission
- `IInvocationDispatchGate.ClaimAsync(InvocationRecord candidate, ToolInvocation normalized, ToolInvocation effective, CancellationToken ct) -> Task<InvocationGateDecision>` only for a newly admitted attempt, inside the pre-dispatch block
- `CollaborationInvocationGate` consumes Tasks 1–2; its trusted binding lookup is always `(workspaceId, invocationId)`, then it compares subject, exact operation/target, original/effective input, envelope, permit, and live agreement

- [ ] Add failing tests for generic HTTP resume after cancellation/revocation, approved UUID without recipient permit, wrong subject plus stripped marker before PrepareTask, failed binding lookup, disabled-route restart with retained proposal, and a changed effective input

```csharp
// Invoke_BoundUuidStrippedMarkerAndWrongSubject_Denies
connectorDispatchCount.ShouldBe(0); result.Success.ShouldBeFalse();
// Execute_AuthorityExpiresDuringGatePersistence_DoesNotDispatch
connectorDispatchCount.ShouldBe(0); record.Attempt.Outcome.ShouldBe(InvocationOutcome.Denied);
// Invoke_OrdinaryUnboundUuid_PreservesExistingBehavior
result.Success.ShouldBeTrue(); connectorDispatchCount.ShouldBe(1);
```

- [ ] Run Tools and Silo modules and verify failures demonstrate the old bypass/stale-authority behavior
- [ ] Make the gate a mandatory ToolActor dependency, never an optional allow-all overload. The Host composes one concrete gate backed by retained binding storage even when A2A ingress is disabled. Resolve known unbound ordinary invocations explicitly; storage failure is never equivalent to no binding. A marker with no matching binding denies
- [ ] In InvocationExecution, call ClaimAsync after journal admission, then repeat existing recipient grant/token, approval expiry, cancellation, connection and target checks after gate I/O. Only then enter the connector-dispatch try. Expected gate denial records a confirmed no-dispatch outcome; recording failure remains uncertain. Duplicate invocations never acquire a new claim or replay
- [ ] Test cancel-wins and claim-wins with controlled gates, store failure, revoked grants/expired approval while persistence is blocked, and ordinary existing tool behavior. Run all affected modules; no assertions/timeouts weakened
- [ ] Commit `feat: enforce collaboration policy at governed dispatch`

### Task 4: Recipient preparation, continuation, and recoverable artifacts

**Files:** Create `src/Collaboration/SubmitTask/SubmitCollaborationTask.cs`, `src/Collaboration/PrepareTask/PrepareCollaborationTask.cs`, `src/Collaboration/ResumeTask/ResumeCollaborationTask.cs`, `src/Collaboration/GetTask/GetCollaborationTask.cs`, `src/Collaboration/ListTasks/ListCollaborationTasks.cs`, `src/Collaboration/CancelTask/CancelCollaborationTask.cs`, and `src/Collaboration/GetTask/CollaborationTaskProjection.cs`; create `tests/Weave.Silo.Tests/Collaboration/{CollaborationLifecycleTests,CollaborationRecoveryTests,CollaborationApprovalTests,CollaborationScenario}.cs`.

**Interfaces:** Public feature handlers, with constructor-injected dependencies:
- `SubmitCollaborationTask.ExecuteAsync(PeerScope scope, string messageId, NoteCommand command, CancellationToken ct) -> Task<CollaborationResult<TaskSnapshot>>`
- `PrepareCollaborationTask.ExecuteAsync(CollaborationTaskId id, CapabilityToken recipientToken, CancellationToken ct) -> Task<CollaborationResult<TaskSnapshot>>`
- `ResumeCollaborationTask.ExecuteAsync(CollaborationTaskId id, CapabilityToken recipientToken, CancellationToken ct) -> Task<CollaborationResult<TaskSnapshot>>`
- `GetCollaborationTask.ExecuteAsync(PeerScope scope, CollaborationTaskId id, CancellationToken ct)` and `CancelCollaborationTask.ExecuteAsync(...)` return the same result type
- `ListCollaborationTasks.ExecuteAsync(PeerScope scope, TaskQuery query, CancellationToken ct) -> Task<CollaborationResult<TaskPage>>`
- `CollaborationTaskProjection.RefreshAsync(TaskSnapshot task, CapabilityToken recipientToken, CancellationToken ct) -> Task<CollaborationResult<TaskSnapshot>>`; consumes public invocation/proposal/approval readers and exact owner-scoped evidence, never neighboring storage internals

Recipient capabilities require the existing `ToolCapability.Connect(plan.ToolName)`, `ToolCapability.Invoke(plan.ToolName, "write_file")`, and `invocation:read` grants, with exact recipient subject/workspace matching. Operator review retains `approval:decide`, `invocation:read`, and `ToolCapability.Approve(plan.ToolName, "write_file")` plus an independent subject; approval grants never appear on the peer capability.

- [ ] Add the A01/A02/A03/A08/A10/A11/A12/A15/A20/A25 tests from the spec using real SQLite, existing ToolActor, independent operator capability, and a dedicated temporary FileSystem root

```csharp
// Prepare_ApprovalRequired_RecordsPendingWithoutAttemptOrFile
approval.State.ShouldBe(InvocationApprovalState.Pending);
attempt.ShouldBeNull(); File.Exists(target).ShouldBeFalse();
// Restart_SucceededInvocationMissingTaskArtifact_RebuildsWithoutDispatch
artifact.Text.ShouldBe(approvedText); writeCount.ShouldBe(1);
// Resume_OutcomeUnknown_IsQueryOnlyAcrossRestart
task.Failure.Retry.ShouldBe(RetryClassification.QueryOnly); writeCount.ShouldBe(1);
```

- [ ] Run Silo tests and capture the intended missing lifecycle/recovery assertions
- [ ] Implement exact frozen note parameters and `weave_collaboration_contract`; reconnect the configured tool when required. Prepare stages the existing approval; only recipient ResumeTask records the continuation permit. Wrap policy-dependent task admission, continuation-permit creation, and dispatch claiming in Task 1's shared current-agreement coordinator; a preceding snapshot read alone is insufficient. Approval remains an independent operation and never mints execution grants. Reuse the original subject/UUID/body and current recipient capability
- [ ] Implement explicit state projection, deterministic authorized artifacts bounded at 24 KiB/two parts, and recovery from confirmed journal success. Unknown/admitted/unavailable evidence is query-only. Reject changed plans and terminal-task messages; never treat a generic 404/timeout as proven pre-admission absence
- [ ] Run concurrent resumers and crash injection at every A12 boundary, including success before projection commit. Assert exactly one effect/attempt and correct rejected/unknown results
- [ ] Commit `feat: complete governed collaboration note lifecycle`

### Task 5: Version-specific HTTP JSON codecs and golden fixtures

**Files:** Create `extensions/Weave.A2A/Weave.A2A.csproj`, `extensions/Weave.A2A/Protocol/{A2AWireRevision,A2AProtocolLimits,IA2AHttpJsonCodec,A2AHttpJsonCodec03,A2AHttpJsonCodec10,A2AHttpRequest,A2AHttpResponse,A2ACardDefinition}.cs`, and `extensions/Weave.A2A/Protocol/V03/A2A03JsonContext.cs` and `V10/A2A10JsonContext.cs` with the version-owned request/card/task/error records alongside each context. Create `protocol/a2a/{0.3,1.0}/{agent-card,send-propose,send-resume,task-pending,task-completed,task-unknown,list,cancel-error}.json`; create `tests/Weave.Tools.Tests/Collaboration/A2AHttpJsonCodecTests.cs`; modify `Weave.slnx` and the Tools test project reference/fixture-copy items. Regenerate affected lockfiles using restore, never hand-edit hashes.

**Interfaces:** `IA2AHttpJsonCodec.DecodeSend(ReadOnlyMemory<byte> body) -> CollaborationResult<A2AHttpRequest>`; `EncodeTask(TaskSnapshot task, int? historyLength) -> A2AHttpResponse`; `EncodeList(TaskPage page, TaskQuery query) -> A2AHttpResponse`; `EncodeError(CollaborationFailure failure) -> A2AHttpResponse`; `EncodeCard(A2ACardDefinition card) -> A2AHttpResponse`. A2AHttpResponse contains HTTP status, exact content type and owned UTF-8 JSON bytes. A2AHttpRequest contains message ID, NoteCommand, version and accepted output/history settings. A2ACardDefinition contains configured public URLs, synthetic label, security scheme and fixed skill; no private roots or tokens.

- [ ] Add golden-fixture tests that inspect independent JSON fields, not only serialize/deserialize round trips

```csharp
// Decode_DuplicateOrCaseVariantAction_RejectsAmbiguity
result.Error.Code.ShouldBe("invalid-request");
// EncodeList_10_ArtifactsNotRequested_OmitsField
json.RootElement.GetProperty("nextPageToken").GetString().ShouldBe("");
json.RootElement.GetProperty("tasks")[0].TryGetProperty("artifacts", out _).ShouldBeFalse();
// EncodeList_03_UsesArray
json.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
```

Also pin 0.3 `kind`/lowercase states/roles and wrapped Send; 1.0 named unions/uppercase enums/error reasons; missing or false 1.0 returnImmediately rejection; 0.3 blocking true rejection; every unsupported optional feature; media types; null bodies; excessive bytes/depth/parts; malformed UTF-8.
- [ ] Run Tools tests and verify intended missing/incorrect shapes fail
- [ ] Implement separate codecs against the pinned spec/type sources. Reject duplicate and case-variant recognized property names using bounded Utf8JsonReader validation before deserialization. Keep exact per-version DTOs and source-generated metadata. Unknown optional data is not executable authority; unknown required extensions fail
- [ ] Run all fixtures and negative cases, verifying cards truthfully advertise only HTTP+JSON and the async reviewed-note profile
- [ ] Commit `feat: add pinned A2A HTTP JSON protocol bindings`

### Task 6: Host composition and a durable loopback client

**Files:** Create `hosts/Weave.Host/Collaboration/{A2AHostOptions,A2APeerAuthenticator,ExtensionsToA2AEndpoints,A2ACardEndpoint,A2ASendEndpoint,A2AGetTaskEndpoint,A2AListTasksEndpoint,A2ACancelTaskEndpoint}.cs`; create `extensions/Weave.A2A/Client/{A2AHttpClient,A2ALoopbackOriginPolicy}.cs`; create `src/Collaboration/SendTask/{SendPeerTask,PeerSendRequest,PeerTaskResult}.cs`; modify `hosts/Weave.Host/Startup/SiloServiceRegistrar.cs`, `SiloApplicationConfigurator.cs`, `hosts/Weave.Host/Weave.Host.csproj`, and affected lockfiles. Create `tests/Weave.Silo.Tests/Collaboration/{A2AHostConfigurationTests,A2AHostAuthorizationTests,A2AHttpIntegrationTests,A2AClientReceiptTests}.cs`.

**Interfaces:**
- `A2APeerAuthenticator.AuthenticateAsync(HttpContext context, string recipientRegistration, CancellationToken ct) -> Task<CollaborationResult<PeerScope>>`; validate capability, registered source/route, and exact operation before lookup
- `ExtensionsToA2AEndpoints.MapA2AEndpoints(WebApplication app) -> void`; disabled by default; map explicit trusted recipient/version bases and cards, with no enrollment, approval, minting, or arbitrary forwarding route
- `PeerSendRequest`: authenticated local source, configured recipient/origin, chosen wire revision, message ID, exact normalized request bytes
- `PeerTaskResult`: remote task/context IDs, normalized public state, bounded shareable artifacts, safe status/failure; never the recipient's frozen plan or internal permit/claim
- `SendPeerTask.ExecuteAsync(PeerSendRequest request, CapabilityToken currentPeerToken, CancellationToken ct) -> Task<CollaborationResult<PeerTaskResult>>`; owns source policy and receipt-before-send ordering
- `A2AHttpClient.SendAsync(OutgoingReceipt receipt, CapabilityToken currentPeerToken, CancellationToken ct) -> Task<CollaborationResult<PeerTaskResult>>`; transport only, no implicit retry

- [ ] Add real HTTP tests for disabled routes; missing/ambiguous authority/store/public signing key; `none` global auth not bypassing peer auth; wrong recipient/task/cursor; exact per-version routes and headers; 0.3 cancel name mismatch; proper 401/403/404/error envelopes
- [ ] Add controlled-response-loss tests and restart source receipt storage before receiving task/context IDs. Assert an explicit identical resubmission returns the original task while changed origin, recipient, body, or version conflicts. Assert failed receipt persistence results in zero network calls
- [ ] Run Silo tests and record intended routing/authentication/transmission-order failures
- [ ] Compose exactly one durable guard and policy provider, independent of route enablement; map version-specific handlers to Task 4 use cases. Client disables cookies, redirects and proxies; accepts only configured literal loopback endpoints; uses 5-second connect/30-second request deadlines. Credentials are per-request and never stored with receipts or followed to an advertised replacement origin
- [ ] Run HTTP boundary cases for 64 KiB, depth 16, unsupported callbacks/files, foreign reference tasks, pagination defaults/limits, escaped IDs, and malformed/duplicate headers. Use no live external peers
- [ ] Commit `feat: expose authenticated A2A collaboration endpoints`

### Task 7: End-to-end proof, release gates, and handoff

**Files:** Create `tests/Weave.Silo.Tests/Collaboration/{A2AEndToEndTests,A2ARecoveryMatrixTests,A2AAdversarialTests}.cs`, `scripts/a2a-fixture-client.py`, `scripts/tests/test_a2a_fixture_client.py`, and `docs/implementation/2026-10-04-a2a-collaboration-foundation.md`. Modify `README.md` only with truthful opt-in usage and limitations, and `tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj` to copy the Python client fixture. Do not modify the Local CLI files.

**Interfaces:** Standard-library Python client accepts `--base-url`, `--wire-version`, `--request-file`, and an inherited capability through a fixture-controlled pipe/environment; it never prints, persists, or sends that capability to another origin. It emits bounded task/state/artifact diagnostics and returns nonzero for protocol/authorization/unknown outcomes. It has no approval or token-minting command.

- [ ] Write and run independent-client failing tests against both real loopback endpoints before completing the client; validate response fields independently of .NET DTOs and validate client redaction with a synthetic credential marker
- [ ] Complete the deterministic fixture and all spec acceptance cases A01–A25. Produce separate approved/rejected/expired runs. Independently inspect exact note bytes using the retained FileSystem encoding contract (including its BOM behavior), SQLite invocation/attempt/approval state, and task correlation after every restart/error test
- [ ] If packaged Codex launch is attempted, first verify A21 at that exact revision, using integrated repairs at `1e8e09f77f9d22d994e392d377ac03ab8a53a0eb` and their Linux/Windows CI only as baseline evidence. If launch-specific verification is unavailable, do not launch; report A21 as unverified and the proof as synthetic HTTP/domain acceptance only. Do not imply a UI/OS sandbox or new real human-login test
- [ ] Run, from the final implementation tree:

```bash
python3 -m unittest discover -s scripts/tests -v
dotnet restore Weave.slnx
dotnet restore Weave.slnx --locked-mode
dotnet build Weave.slnx --no-restore -c Release
dotnet test --solution Weave.slnx --no-build -c Release
```

Run the exact full-solution formatting command and existing exclusion set from `.github/workflows/ci.yml`. Inspect every skip and failed stage. Because this change touches the Orleans bridge, repeat the full suite three times per repository review guidance. Preserve evidence of failed attempts; no longer timeouts, new skips, assertions weakened, vulnerability suppression, or hand-edited lock hashes.
- [ ] Apply check-rules and adversarial-review procedures, then a fresh whole-branch review covering both approved spec and final diff. Require explicit review of common-ingress gating, persisted revocation, source receipts, secret substitution, task data leakage, and version-shape differences
- [ ] Record exact tested commit, observed states, commands, skipped/unrun stages, independent-client scope, and residual A21 prerequisite in the implementation record. Commit `test: verify governed A2A collaboration end to end`
- [ ] Open/update a draft implementation PR only after authorized execution. Verify pushed SHA and monitor its exact-head CI to terminal, fixing only authorized in-scope failures. Coordinate repository merges under the user's current authorization only after exact-head checks. Deployment, release, and broader production-readiness claims remain outside this plan

## Coverage and plan self review

- Spec sections 1–6: Tasks 1–3 and 6; existing identity/authority boundaries and fixed skill preserved
- Spec sections 7–8: Tasks 2–4; real persistence, independent approval, every-ingress gate, source receipts, cancellation and recovery covered
- Spec sections 9–10: Tasks 5–6; both selected revisions, async restrictions, truthful cards, exact limits and network isolation covered
- Spec section 11: A01/A02/A03/A08/A10/A11/A12/A15/A20/A25 in Task 4; A04/A05/A06/A14/A16/A17/A18/A19/A22/A23 in Tasks 2/5/6; A07 in Task 2; A09/A13/A24 in Task 3; A21 separately gated in Task 7
- Spec sections 12–13: Global Constraints and Task 7 preserve non-goals and review gates
- Review Focus cases are assigned explicitly to their owning test tasks
- Type/interface names are consistent across tasks; provider APIs remain synchronous only for short local SQLite transactions, asynchronous orchestration propagates cancellation
- This plan contains implementation decisions, test assertions, and verification commands. It is not an implementation transcript or evidence of passed product tests

## Requested decision

Review this plan and select **subagent-driven execution** (recommended) or **native execution with a whole-branch review**. Approval can cover the whole plan; individual tasks do not require repeated user confirmations unless scope, external risk, permissions, or a material design decision changes. Repository merges follow the user's current authorization and verified checks; no renewed approval per commit is implied. Existing scope limits for deployment, credentials, and real external peers remain in force.
