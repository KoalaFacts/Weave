# PodmanRuntime Testability — ICommandRunner Extraction

**Date:** 2026-04-05
**Status:** Approved
**Scope:** Extract process execution from `PodmanRuntime` behind `ICommandRunner` interface, add unit tests for argument building, add integration tests with real podman.

## Problem

`PodmanRuntime` (162 lines, 12 branches) has zero test coverage. Every method calls `RunPodmanAsync` which shells out to `podman` via `Process.Start`. The argument-building logic (security flags, env vars, port mappings, network config) cannot be verified without the podman binary installed.

## Solution

### ICommandRunner Abstraction

**File:** `src/Workspaces/Weave.Workspaces/Runtime/ICommandRunner.cs`

```csharp
public interface ICommandRunner
{
    Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct);
}
```

Single method. Returns stdout. Throws `InvalidOperationException` on non-zero exit code (preserving current `RunPodmanAsync` behavior).

### ProcessCommandRunner — Production Implementation

**File:** `src/Workspaces/Weave.Workspaces/Runtime/ProcessCommandRunner.cs`

Wraps `Process.Start` with stdout/stderr capture, exit code checking, and the `InvalidOperationException` throw on failure. This is the current `RunPodmanAsync` logic extracted verbatim. Registered in DI as the default `ICommandRunner`.

### PodmanRuntime Change

Constructor changes from `PodmanRuntime(ILogger<PodmanRuntime> logger)` to `PodmanRuntime(ICommandRunner runner, ILogger<PodmanRuntime> logger)`. The `RunPodmanAsync` method becomes a one-liner delegating to `_runner.RunAsync("podman", argList, ct)`.

### StubCommandRunner — Test Double

**File:** lives in `Weave.Workspaces.Tests` (not in production code).

Captures all invocations into a `List<(string Command, IReadOnlyList<string> Arguments)>`. Returns configurable output per call (default: empty string). Can be configured to throw for error-path testing.

## Unit Tests

**File:** `src/Workspaces/Weave.Workspaces.Tests/PodmanRuntimeTests.cs`

All tests use `StubCommandRunner` — no podman binary needed, runs in CI.

### StartContainerAsync argument building:
- `StartContainerAsync_BasicSpec_BuildsCorrectArgs` — verifies `run -d --name {name} --cap-drop=ALL {image}`
- `StartContainerAsync_WithNetwork_AddsNetworkFlag` — verifies `--network {id}` present
- `StartContainerAsync_ReadOnly_AddsFlag` — verifies `--read-only` present
- `StartContainerAsync_NoNetwork_AddsFlag` — verifies `--network=none` present
- `StartContainerAsync_WithEnvVars_AddsEachPair` — verifies `-e KEY=VAL` for each env var
- `StartContainerAsync_WithPortMappings_AddsPFlag` — verifies `-p 8080:80` for each mapping
- `StartContainerAsync_WithCommand_AppendsAfterImage` — verifies command args appear after image name

### StopContainerAsync:
- `StopContainerAsync_CallsStopThenRm` — verifies two calls: `stop {id}` then `rm -f {id}`

### CreateNetworkAsync:
- `CreateNetworkAsync_BasicSpec_BuildsArgs` — verifies `network create {name}`
- `CreateNetworkAsync_WithSubnet_AddsFlag` — verifies `--subnet {subnet}` present

### DeleteNetworkAsync:
- `DeleteNetworkAsync_CallsNetworkRm` — verifies `network rm -f {id}`

### TeardownAsync:
- `TeardownAsync_StopsContainersAndRemovesNetwork` — stub returns container IDs from `ps`, verifies stop/rm calls for each, then network rm

### ProvisionAsync:
- `ProvisionAsync_SkipsNonMcpTools` — only MCP-type tools create containers
- `ProvisionAsync_CreatesNetworkFromManifest` — network name uses `{workspace}` template substitution

### Error handling:
- `RunAsync_NonZeroExitCode_Throws` — stub configured to throw → `InvalidOperationException` propagates

## Integration Tests

**File:** `src/Workspaces/Weave.Workspaces.Tests/PodmanRuntimeIntegrationTests.cs`

All tests gated with `[Trait("Category", "Integration")]`. Use `ProcessCommandRunner` with real podman. Each test creates resources with unique names and cleans up in a `finally` block.

- `CreateNetwork_ThenDelete_Succeeds` — creates network, verifies via `podman network ls`, deletes
- `StartContainer_ThenStop_Succeeds` — starts `alpine:latest` with `sleep 30`, verifies via `podman ps`, stops/removes
- `StartContainer_WithEnvVars_PassedToContainer` — starts alpine, verifies env vars via `podman exec ... env`
- `ProvisionAsync_FullLifecycle_CreatesAndTearsDown` — full provision → teardown with minimal manifest

### Running tests

```bash
# Unit tests only (CI default — no podman needed)
dotnet test --solution Weave.slnx

# Integration tests (requires podman)
dotnet test --project src/Workspaces/Weave.Workspaces.Tests --filter "Category=Integration"
```

The default `dotnet test --solution Weave.slnx` does NOT run integration tests unless explicitly filtered.

## DI Registration

`ProcessCommandRunner` is registered as `ICommandRunner` in `src/Runtime/Weave.Silo/Program.cs` alongside the existing `PodmanRuntime` registration. If `PodmanRuntime` is not already registered there, it will be registered as `IWorkspaceRuntime` when the podman runtime is selected.

## Out of Scope

- Changing `IWorkspaceRuntime` interface
- Changing `InProcessRuntime` (it has no process execution)
- Adding Docker/containerd support
- Container image pulling or registry auth
