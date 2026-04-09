# PodmanRuntime Testability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Make `PodmanRuntime` testable by extracting process execution behind `ICommandRunner`, then add 15 unit tests and 4 integration tests.

**Architecture:** `ICommandRunner` interface abstracts process execution. `ProcessCommandRunner` is the production implementation (extracted from `RunPodmanAsync`). `StubCommandRunner` captures invocations for unit tests. Integration tests use real podman with `[Trait("Category", "Integration")]`.

**Tech Stack:** C# / .NET 10, xunit.v3 + Shouldly, podman (integration tests only)

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/Workspaces/Weave.Workspaces/Runtime/ICommandRunner.cs` | Interface for process execution |
| Create | `src/Workspaces/Weave.Workspaces/Runtime/ProcessCommandRunner.cs` | Production implementation wrapping Process.Start |
| Modify | `src/Workspaces/Weave.Workspaces/Runtime/PodmanRuntime.cs` | Inject ICommandRunner, delegate RunPodmanAsync |
| Modify | `src/Runtime/Weave.Silo/Program.cs` | Register ProcessCommandRunner in DI |
| Create | `src/Workspaces/Weave.Workspaces.Tests/PodmanRuntimeTests.cs` | Unit tests with StubCommandRunner |
| Create | `src/Workspaces/Weave.Workspaces.Tests/PodmanRuntimeIntegrationTests.cs` | Integration tests with real podman |

---

### Task 1: ICommandRunner Interface and ProcessCommandRunner

**Files:**
- Create: `src/Workspaces/Weave.Workspaces/Runtime/ICommandRunner.cs`
- Create: `src/Workspaces/Weave.Workspaces/Runtime/ProcessCommandRunner.cs`

- [ ] **Step 1: Create the ICommandRunner interface**

```csharp
// src/Workspaces/Weave.Workspaces/Runtime/ICommandRunner.cs
namespace Weave.Workspaces.Runtime;

public interface ICommandRunner
{
    Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct);
}
```

- [ ] **Step 2: Create ProcessCommandRunner**

Extract the current `RunPodmanAsync` logic from `PodmanRuntime.cs` (lines 123-151) into a standalone class. Replace the hardcoded `"podman"` filename with the `command` parameter:

```csharp
// src/Workspaces/Weave.Workspaces/Runtime/ProcessCommandRunner.cs
using System.Diagnostics;

namespace Weave.Workspaces.Runtime;

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{command}'.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command '{command}' failed (exit code {process.ExitCode}): {stderr}");

        return stdout;
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Workspaces/Weave.Workspaces/Weave.Workspaces.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

Message: `feat: add ICommandRunner interface and ProcessCommandRunner`

---

### Task 2: Rewire PodmanRuntime to Use ICommandRunner

**Files:**
- Modify: `src/Workspaces/Weave.Workspaces/Runtime/PodmanRuntime.cs`
- Modify: `src/Runtime/Weave.Silo/Program.cs`

- [ ] **Step 1: Update PodmanRuntime constructor and RunPodmanAsync**

Change the constructor to accept `ICommandRunner` and delegate `RunPodmanAsync` to it. Remove the `System.Diagnostics` using and all the `Process.Start` code.

The constructor changes from:
```csharp
public sealed partial class PodmanRuntime(ILogger<PodmanRuntime> logger) : IWorkspaceRuntime
```
to:
```csharp
public sealed partial class PodmanRuntime(ICommandRunner runner, ILogger<PodmanRuntime> logger) : IWorkspaceRuntime
```

The `RunPodmanAsync` method (lines 123-151) becomes:
```csharp
private async Task<string> RunPodmanAsync(IEnumerable<string> arguments, CancellationToken ct)
{
    var argList = arguments.ToList();
    LogPodmanCommand(string.Join(" ", argList));
    return await runner.RunAsync("podman", argList, ct);
}
```

Remove `using System.Diagnostics;` from the top of the file (no longer needed). Keep the `_logger` field assignment — but note: after the refactor, `_logger` is only used by the `[LoggerMessage]` partial methods which access `logger` from the primary constructor directly. Check if `_logger` is still needed. If the `[LoggerMessage]` attributes generate code that uses `_logger`, keep it. If they use `logger` from the primary constructor, remove the `_logger = logger` line and the `_logger` field.

- [ ] **Step 2: Register ProcessCommandRunner in DI**

In `src/Runtime/Weave.Silo/Program.cs`, add the registration before the PodmanRuntime line. Find the block around line 48-51:

```csharp
if (isLocalMode)
    builder.Services.AddSingleton<IWorkspaceRuntime, InProcessRuntime>();
else
    builder.Services.AddSingleton<IWorkspaceRuntime, PodmanRuntime>();
```

Add the ICommandRunner registration before it:
```csharp
builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();

if (isLocalMode)
    builder.Services.AddSingleton<IWorkspaceRuntime, InProcessRuntime>();
else
    builder.Services.AddSingleton<IWorkspaceRuntime, PodmanRuntime>();
```

Add the necessary using at the top of Program.cs:
```csharp
using Weave.Workspaces.Runtime;
```

- [ ] **Step 3: Build the full solution**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

Message: `refactor: rewire PodmanRuntime to use ICommandRunner`

---

### Task 3: Unit Tests with StubCommandRunner

**Files:**
- Create: `src/Workspaces/Weave.Workspaces.Tests/PodmanRuntimeTests.cs`

- [ ] **Step 1: Create StubCommandRunner and PodmanRuntimeTests**

The `StubCommandRunner` is a test double that captures all invocations and returns configurable output. It lives inside the test file as a nested class.

```csharp
// src/Workspaces/Weave.Workspaces.Tests/PodmanRuntimeTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

public sealed class PodmanRuntimeTests
{
    private readonly StubCommandRunner _stub = new();
    private readonly PodmanRuntime _runtime;

    public PodmanRuntimeTests()
    {
        _runtime = new PodmanRuntime(_stub, NullLogger<PodmanRuntime>.Instance);
    }

    // --- StartContainerAsync ---

    [Fact]
    public async Task StartContainerAsync_BasicSpec_BuildsCorrectArgs()
    {
        _stub.NextOutput = "abc123\n";
        var spec = new ContainerSpec { Name = "test-ctr", Image = "alpine:latest" };

        await _runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        var args = _stub.Invocations[0].Arguments;
        args.ShouldContain("run");
        args.ShouldContain("-d");
        args.ShouldContain("--name");
        args.ShouldContain("test-ctr");
        args.ShouldContain("--cap-drop=ALL");
        args.ShouldContain("alpine:latest");
    }

    [Fact]
    public async Task StartContainerAsync_WithNetwork_AddsNetworkFlag()
    {
        _stub.NextOutput = "abc123\n";
        var spec = new ContainerSpec
        {
            Name = "ctr", Image = "alpine",
            NetworkId = NetworkId.From("my-net")
        };

        await _runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        var args = _stub.Invocations[0].Arguments;
        var netIdx = args.IndexOf("--network");
        netIdx.ShouldBeGreaterThan(-1);
        args[netIdx + 1].ShouldBe("my-net");
    }

    [Fact]
    public async Task StartContainerAsync_ReadOnly_AddsFlag()
    {
        _stub.NextOutput = "abc123\n";
        var spec = new ContainerSpec
        {
            Name = "ctr", Image = "alpine",
            ReadOnly = true
        };

        await _runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        _stub.Invocations[0].Arguments.ShouldContain("--read-only");
    }

    [Fact]
    public async Task StartContainerAsync_NoNetwork_AddsFlag()
    {
        _stub.NextOutput = "abc123\n";
        var spec = new ContainerSpec
        {
            Name = "ctr", Image = "alpine",
            NoNetwork = true
        };

        await _runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        _stub.Invocations[0].Arguments.ShouldContain("--network=none");
    }

    [Fact]
    public async Task StartContainerAsync_WithEnvVars_AddsEachPair()
    {
        _stub.NextOutput = "abc123\n";
        var spec = new ContainerSpec
        {
            Name = "ctr", Image = "alpine",
            Environment = new() { ["DB_HOST"] = "localhost", ["DB_PORT"] = "5432" }
        };

        await _runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        var args = _stub.Invocations[0].Arguments;
        args.ShouldContain("-e");
        args.ShouldContain("DB_HOST=localhost");
        args.ShouldContain("DB_PORT=5432");
    }

    [Fact]
    public async Task StartContainerAsync_WithPortMappings_AddsPFlag()
    {
        _stub.NextOutput = "abc123\n";
        var spec = new ContainerSpec
        {
            Name = "ctr", Image = "alpine",
            PortMappings = new() { [8080] = 80 }
        };

        await _runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        var args = _stub.Invocations[0].Arguments;
        args.ShouldContain("-p");
        args.ShouldContain("8080:80");
    }

    [Fact]
    public async Task StartContainerAsync_WithCommand_AppendsAfterImage()
    {
        _stub.NextOutput = "abc123\n";
        var spec = new ContainerSpec
        {
            Name = "ctr", Image = "alpine",
            Command = ["echo", "hello"]
        };

        await _runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken);

        var args = _stub.Invocations[0].Arguments;
        var imageIdx = args.IndexOf("alpine");
        args[imageIdx + 1].ShouldBe("echo");
        args[imageIdx + 2].ShouldBe("hello");
    }

    // --- StopContainerAsync ---

    [Fact]
    public async Task StopContainerAsync_CallsStopThenRm()
    {
        var id = ContainerId.From("abc123");

        await _runtime.StopContainerAsync(id, TestContext.Current.CancellationToken);

        _stub.Invocations.Count.ShouldBe(2);
        _stub.Invocations[0].Arguments.ShouldContain("stop");
        _stub.Invocations[0].Arguments.ShouldContain("abc123");
        _stub.Invocations[1].Arguments.ShouldContain("rm");
        _stub.Invocations[1].Arguments.ShouldContain("-f");
        _stub.Invocations[1].Arguments.ShouldContain("abc123");
    }

    // --- CreateNetworkAsync ---

    [Fact]
    public async Task CreateNetworkAsync_BasicSpec_BuildsArgs()
    {
        _stub.NextOutput = "net-id-123\n";
        var spec = new NetworkSpec { Name = "test-net" };

        var handle = await _runtime.CreateNetworkAsync(spec, TestContext.Current.CancellationToken);

        var args = _stub.Invocations[0].Arguments;
        args.ShouldContain("network");
        args.ShouldContain("create");
        args.ShouldContain("test-net");
        handle.Name.ShouldBe("test-net");
    }

    [Fact]
    public async Task CreateNetworkAsync_WithSubnet_AddsFlag()
    {
        _stub.NextOutput = "net-id-123\n";
        var spec = new NetworkSpec { Name = "test-net", Subnet = "10.0.0.0/24" };

        await _runtime.CreateNetworkAsync(spec, TestContext.Current.CancellationToken);

        var args = _stub.Invocations[0].Arguments;
        args.ShouldContain("--subnet");
        args.ShouldContain("10.0.0.0/24");
    }

    // --- DeleteNetworkAsync ---

    [Fact]
    public async Task DeleteNetworkAsync_CallsNetworkRm()
    {
        var id = NetworkId.From("net-123");

        await _runtime.DeleteNetworkAsync(id, TestContext.Current.CancellationToken);

        var args = _stub.Invocations[0].Arguments;
        args.ShouldContain("network");
        args.ShouldContain("rm");
        args.ShouldContain("-f");
        args.ShouldContain("net-123");
    }

    // --- TeardownAsync ---

    [Fact]
    public async Task TeardownAsync_StopsContainersAndRemovesNetwork()
    {
        // First call (ps) returns two container IDs, subsequent calls return empty
        _stub.OutputQueue.Enqueue("ctr-1\nctr-2\n");

        await _runtime.TeardownAsync(WorkspaceId.From("ws-1"), TestContext.Current.CancellationToken);

        // ps + stop ctr-1 + rm ctr-1 + stop ctr-2 + rm ctr-2 + network rm = 6 calls
        _stub.Invocations.Count.ShouldBe(6);
        _stub.Invocations[0].Arguments.ShouldContain("ps");
        _stub.Invocations[^1].Arguments.ShouldContain("network");
    }

    // --- ProvisionAsync ---

    [Fact]
    public async Task ProvisionAsync_SkipsNonMcpTools()
    {
        _stub.NextOutput = "net-123\n"; // CreateNetwork
        var manifest = new WorkspaceManifest
        {
            Version = "1", Name = "ws-test",
            Tools = new()
            {
                ["cli-tool"] = new ToolDefinition { Type = "cli" },
                ["openapi-tool"] = new ToolDefinition { Type = "openapi" }
            }
        };

        var env = await _runtime.ProvisionAsync(manifest, TestContext.Current.CancellationToken);

        // Only CreateNetwork call, no StartContainer calls
        env.Containers.ShouldBeEmpty();
        _stub.Invocations.Count.ShouldBe(1); // just network create
    }

    [Fact]
    public async Task ProvisionAsync_CreatesNetworkFromManifest()
    {
        _stub.NextOutput = "net-123\n";
        var manifest = new WorkspaceManifest
        {
            Version = "1", Name = "my-ws",
            Workspace = new WorkspaceConfig
            {
                Network = new NetworkConfig { Name = "custom-{workspace}-net" }
            }
        };

        await _runtime.ProvisionAsync(manifest, TestContext.Current.CancellationToken);

        var args = _stub.Invocations[0].Arguments;
        args.ShouldContain("custom-my-ws-net");
    }

    // --- Error handling ---

    [Fact]
    public async Task RunAsync_NonZeroExitCode_PropagatesException()
    {
        _stub.ThrowOnNextCall = new InvalidOperationException("podman failed (exit code 1): error");
        var spec = new ContainerSpec { Name = "ctr", Image = "alpine" };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _runtime.StartContainerAsync(spec, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("exit code 1");
    }

    // --- Stub ---

    private sealed class StubCommandRunner : ICommandRunner
    {
        public List<(string Command, IReadOnlyList<string> Arguments)> Invocations { get; } = [];
        public string NextOutput { get; set; } = "";
        public Queue<string> OutputQueue { get; } = new();
        public InvalidOperationException? ThrowOnNextCall { get; set; }

        public Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            if (ThrowOnNextCall is { } ex)
            {
                ThrowOnNextCall = null;
                return Task.FromException<string>(ex);
            }

            Invocations.Add((command, arguments.ToList()));
            var output = OutputQueue.Count > 0 ? OutputQueue.Dequeue() : NextOutput;
            return Task.FromResult(output);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test --project src/Workspaces/Weave.Workspaces.Tests --filter "PodmanRuntimeTests"`
Expected: All 15 tests PASS

- [ ] **Step 3: Commit**

Message: `test: add PodmanRuntime unit tests with StubCommandRunner`

---

### Task 4: Integration Tests with Real Podman

**Files:**
- Create: `src/Workspaces/Weave.Workspaces.Tests/PodmanRuntimeIntegrationTests.cs`

- [ ] **Step 1: Create integration tests**

All tests use `[Trait("Category", "Integration")]` so they are excluded from `dotnet test --solution Weave.slnx` by default. Each test generates a unique name using a GUID suffix and cleans up in a `finally` block.

```csharp
// src/Workspaces/Weave.Workspaces.Tests/PodmanRuntimeIntegrationTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

[Trait("Category", "Integration")]
public sealed class PodmanRuntimeIntegrationTests
{
    private readonly ProcessCommandRunner _runner = new();
    private readonly PodmanRuntime _runtime;

    public PodmanRuntimeIntegrationTests()
    {
        _runtime = new PodmanRuntime(_runner, NullLogger<PodmanRuntime>.Instance);
    }

    [Fact]
    public async Task CreateNetwork_ThenDelete_Succeeds()
    {
        var name = $"weave-test-{Guid.NewGuid():N}"[..30];
        NetworkHandle? handle = null;
        try
        {
            handle = await _runtime.CreateNetworkAsync(
                new NetworkSpec { Name = name },
                TestContext.Current.CancellationToken);

            handle.Name.ShouldBe(name);

            // Verify it exists via podman network ls
            var output = await _runner.RunAsync("podman",
                ["network", "ls", "--format", "{{.Name}}"],
                TestContext.Current.CancellationToken);
            output.ShouldContain(name);
        }
        finally
        {
            if (handle is not null)
                await _runtime.DeleteNetworkAsync(handle.NetworkId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartContainer_ThenStop_Succeeds()
    {
        var name = $"weave-test-{Guid.NewGuid():N}"[..30];
        ContainerHandle? handle = null;
        try
        {
            handle = await _runtime.StartContainerAsync(
                new ContainerSpec
                {
                    Name = name,
                    Image = "alpine:latest",
                    Command = ["sleep", "30"],
                    DropAllCapabilities = false
                },
                TestContext.Current.CancellationToken);

            handle.Name.ShouldBe(name);

            // Verify it's running
            var output = await _runner.RunAsync("podman",
                ["ps", "--filter", $"name={name}", "--format", "{{.Names}}"],
                TestContext.Current.CancellationToken);
            output.Trim().ShouldBe(name);
        }
        finally
        {
            if (handle is not null)
                await _runtime.StopContainerAsync(handle.ContainerId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartContainer_WithEnvVars_PassedToContainer()
    {
        var name = $"weave-test-{Guid.NewGuid():N}"[..30];
        ContainerHandle? handle = null;
        try
        {
            handle = await _runtime.StartContainerAsync(
                new ContainerSpec
                {
                    Name = name,
                    Image = "alpine:latest",
                    Command = ["sleep", "30"],
                    Environment = new() { ["WEAVE_TEST_VAR"] = "hello123" },
                    DropAllCapabilities = false
                },
                TestContext.Current.CancellationToken);

            // Verify env var is set inside the container
            var output = await _runner.RunAsync("podman",
                ["exec", name, "env"],
                TestContext.Current.CancellationToken);
            output.ShouldContain("WEAVE_TEST_VAR=hello123");
        }
        finally
        {
            if (handle is not null)
                await _runtime.StopContainerAsync(handle.ContainerId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ProvisionAsync_FullLifecycle_CreatesAndTearsDown()
    {
        var wsName = $"weave-itest-{Guid.NewGuid():N}"[..25];
        WorkspaceEnvironment? env = null;
        try
        {
            var manifest = new WorkspaceManifest
            {
                Version = "1",
                Name = wsName,
                Tools = new()
                {
                    ["sleep-tool"] = new ToolDefinition
                    {
                        Type = "mcp",
                        Mcp = new McpConfig
                        {
                            Server = "alpine:latest",
                            Args = ["sleep", "30"]
                        }
                    }
                }
            };

            env = await _runtime.ProvisionAsync(manifest, TestContext.Current.CancellationToken);

            env.Containers.Count.ShouldBe(1);
            env.Containers[0].Name.ShouldBe($"weave-{wsName}-sleep-tool");

            // Verify container is running
            var output = await _runner.RunAsync("podman",
                ["ps", "--filter", $"name=weave-{wsName}", "--format", "{{.Names}}"],
                TestContext.Current.CancellationToken);
            output.ShouldContain($"weave-{wsName}-sleep-tool");
        }
        finally
        {
            if (env is not null)
                await _runtime.TeardownAsync(WorkspaceId.From(wsName), CancellationToken.None);
        }
    }
}
```

- [ ] **Step 2: Run integration tests to verify they pass**

Run: `dotnet test --project src/Workspaces/Weave.Workspaces.Tests --filter "Category=Integration"`
Expected: All 4 tests PASS (requires podman installed and running)

- [ ] **Step 3: Run full unit test suite to verify integration tests are excluded**

Run: `dotnet test --solution Weave.slnx`
Expected: All tests PASS. Integration tests should NOT be in the count (xunit `[Trait]` alone does not filter — see Step 4).

- [ ] **Step 4: Add test filter to exclude integration tests from default runs**

Check if the default `dotnet test --solution Weave.slnx` actually excludes `[Trait("Category", "Integration")]` tests. xunit.v3 does NOT auto-exclude traits — you need a filter. If integration tests run by default, add a `.runsettings` file or document the filter in CLAUDE.md:

Add to CLAUDE.md under "Build and Test":
```
# Run integration tests (requires podman)
dotnet test --project src/Workspaces/Weave.Workspaces.Tests --filter "Category=Integration"
```

If integration tests DO run by default and we want to exclude them, the simplest approach is to add `xunit.runner.json` to the test project:
```json
{
  "methodDisplay": "classAndMethod"
}
```
But xunit.v3 doesn't support trait-based exclusion in runner config. Instead, the CI pipeline should use `--filter "Category!=Integration"`. For now, document the convention.

- [ ] **Step 5: Commit**

Message: `test: add PodmanRuntime integration tests with real podman`
