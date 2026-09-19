# Operation authority implementation plan

**Goal:** Replace implicit whole-tool execution authority with explicit operation grants on the existing ToolActor path, including the optional Agent Runtime.

**Architecture:** Authority owns grant construction and restriction. Invocations owns normalization, authorization, execution and revalidation. Each opt-in connector normalizes its own execution selector; no provider switch is added to product code.

**Tech stack:** Existing .NET 10.0.201, C#, xunit.v3, Shouldly, NSubstitute, source-generated Orleans bridges; no new packages.

**Spec:** ARCHITECTURE.md sections 3–7, 11–14. This is one prerequisite slice, not the complete durable Invocation/Approval system.

## Global constraints

- Feature folders remain direct children of src/; contracts stay with their features.
- No alternate ungoverned execution route; retain token validation, workspace checks, leak scanning, redaction and cancellation.
- Keep warnings-as-errors, locked restoration and dependency audit; no vulnerability suppression.
- Existing workspace-bound Agent identity remains unchanged. No persisted-state reset or package publication.
- Missing operation authority denies; Tools lists do not mint authority by themselves.

## Decisions and compatibility

Operation grants use `tool:<escaped-tool>:invoke:<escaped-operation>`. Connection uses `tool:<escaped-tool>:connect`. Components are escaped separately; a delimiter or literal wildcard in a provider name must not turn into a scope wildcard. Deliberate `tool:*` remains broad administrative authority. `tool:files` is no longer execution permission; administrators replace it with exact operations or an explicit `tool:files:invoke:*`.

Connectors normalize the execution selector before authorization. In particular, the current CLI connector exposes a single privileged `exec` operation; calling arbitrary shell text `read_file` cannot give it read-only semantics. Filesystem names are normalized to match its existing case-insensitive dispatch. MCP/OpenAPI keep their exact upstream operation names. This increment does not certify schemas or immutable installation revisions.

Agent Tools declares availability; Agent Capabilities declares authority. Registry tokens are restricted to the requested tool's invocation namespace and never acquire connection or secret grants just because a tool is listed. Added persisted capability metadata defaults empty for old records. Reapply the reviewed manifest/reactivate the Agent to populate it; do not synthesize authority for old records.

Already issued bearer tokens keep their documented expiry/revocation semantics. Reconfiguration restricts subsequently resolved tokens; distributed immediate invalidation of all previously issued tokens is not claimed. Revoke old tokens during rollout where required.

## Review focus

1. Wildcards/delimiters in operation names cannot cross tools or turn a read grant into execution authority.
2. The actual CLI operation, not a caller-supplied label, determines its grant.
3. Mutating input or revoking a token during awaited secret resolution cannot change what was authorized or dispatch with invalid authority.
4. Listing a tool without Capabilities, including restored pre-change records, must not mint a broad token.
5. Actor/handle mismatch, reconnection during an await and cancellation prevent dispatch to the wrong target.

## Task 1 — execution boundary

Files: src/Authority/ToolCapability.cs; src/Invocations/IToolConnector.cs; src/Invocations/InvokeTool/ToolActor.cs and ToolActorIdentity.cs; the six opt-in connector implementations; tests/Weave.Tools.Tests/ToolOperationAuthorizationTests.cs.

Interfaces: retain ToolActor.InvokeAsync(ToolInvocation, CapabilityToken); add `ToolInvocation NormalizeInvocation(ToolInvocation invocation)` to IToolConnector; add pure ToolCapability.Connect/Invoke/ConstrainInvocations helpers.

- [ ] Run the test-first real-file/CLI cases before implementation; exact-read positive cases and old-whole-tool/mutation/revocation negative cases must fail for the expected behavior.
- [ ] Capture owned invocation/token collections before awaits, normalize through the connector, enforce the exact grant and revalidate immediately before dispatch.
- [ ] Capture and verify connection identity; forward cancellation into connector calls.
- [ ] Assert file contents and authorization outcomes, not just mock call counts. Run the full suite afterward.

Example intended grant: `ToolCapability.Invoke("files", "read_file") == "tool:files:invoke:read_file"`.

## Task 2 — Agent Runtime composition

Files: extensions/Weave.AgentRuntime/PluginConnections/{IToolRegistryActor,ToolRegistryState,ToolRegistryActor,ToolRegistryConnector}.cs; extensions/Weave.AgentRuntime/Agents/ActivateAgent/ActivateAgentCommand.cs; hosts/Weave.Host/Api/StartWorkspaceHandler.cs; hosts/Weave.Host/VirtualActors/Tools/ToolRegistryActorGrain.cs; affected registry/host tests and built-in manifest examples.

Interfaces: access configuration receives tool availability plus explicit capabilities. Existing stored availability is retained; an added capability dictionary carries separate authority. ToolCapability.ConstrainInvocations produces only the requested tool's invocation grants.

- [ ] Add regressions for listed-but-ungranted tools, exact read grants, wildcard restriction, replacement/clearing, copy ownership and cross-workspace behavior before changing registry behavior.
- [ ] Propagate reviewed manifest Capabilities at workspace start and Agent activation; use a connect-only token for setup.
- [ ] Resolve only permitted tools, preserve operation restrictions and recheck after reconnect awaits before minting.
- [ ] Verify the actual Orleans runtime boot/serialization path through the existing host suite, not only direct actor calls.

## Task 3 — verification and delivery

- [ ] Keep the pre-existing MCP HTTP SSE baseline failure visible and gather child-process diagnostics. Do not hide it with retries or skipped assertions.
- [ ] Run `python3 -m unittest discover -s scripts/tests -v`.
- [ ] Run `dotnet restore Weave.slnx --locked-mode` and `dotnet build Weave.slnx --no-restore -c Release` with audit enabled.
- [ ] Run `dotnet test --solution Weave.slnx --no-build -c Release` and inspect TRX results. Repeat full runs for actor-interface changes, recording all outcomes.
- [ ] Run `dotnet format Weave.slnx --no-restore --verify-no-changes`.
- [ ] Self-review against check-rules and adversarial-review; record limitations, verified commit and CI evidence in the PR.

## Execution record

Base: `5128f29549590a07365bb0af08003366b7bc80e5`.
Isolated branch: `feature/operation-authority`.
Baseline workflow `35438234889`: locked audit and build passed; the existing EchoMcpHttpTransportSmokeTests SSE case failed with a transport send error before product changes. This is not an operation-authority regression and remains to be diagnosed.

Ruling: local environment has no .NET SDK and cannot resolve github.com; source is obtained through the authorized GitHub artifact connector, edited locally, and compiled/tested on an exact-commit GitHub Actions runner. No local .NET success is claimed.

Ruling: continue the already approved architecture and implementation delegation without requesting another design confirmation. Preserve security gates and reviewable commits; publish/deploy is outside this work.
