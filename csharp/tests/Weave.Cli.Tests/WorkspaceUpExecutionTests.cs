using Weave.Actions.SystemInfo;
using Weave.Actions.Workspace;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class WorkspaceUpExecutionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_ServerAvailableOrStarted_PostsPreparedManifestAndPersistsNewId(bool initiallyReachable)
    {
        using var fixture = new DataTransferFixture();
        await WriteManifestAsync(fixture);
        var original = await File.ReadAllTextAsync(fixture.ManifestPath, TestContext.Current.CancellationToken);
        var capabilityPath = Path.Join(fixture.Root, "capability.txt");
        await File.WriteAllTextAsync(capabilityPath, "  encoded-token\n", TestContext.Current.CancellationToken);
        var launcher = new RecordingLauncher { Start = _ => Task.FromResult(true) };
        fixture.Respond = path => path switch
        {
            "/health" => DataTransferFixture.Response(initiallyReachable ? 200 : 503),
            "/api/workspaces" => Started(),
            _ => throw new InvalidOperationException(path)
        };

        var result = await Command(fixture, launcher).ExecuteAsync(
            new WorkspaceUpOptions("source", "local", capabilityPath), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        var request = fixture.Requests.Single(request => request.Path == "/api/workspaces");
        request.Method.ShouldBe("POST");
        request.Capability.ShouldBe("encoded-token");
        var manifest = request.Body.ShouldNotBeNull()["manifest"].ShouldNotBeNull();
        manifest["name"].ShouldNotBeNull().GetValue<string>().ShouldBe("source");
        var agent = manifest["agents"].ShouldNotBeNull()["assistant"].ShouldNotBeNull();
        agent["systemPromptFile"].ShouldNotBeNull().GetValue<string>()
            .ShouldBe(Path.GetFullPath(Path.Join(fixture.Root, "prompts", "assistant.md")));
        agent["capabilities"].ShouldNotBeNull().AsArray().Select(value => value.ShouldNotBeNull().GetValue<string>())
            .ShouldBe(["tool:files:invoke:read"]);
        (await File.ReadAllTextAsync(WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath),
            TestContext.Current.CancellationToken)).ShouldBe("new-workspace-id");
        (await File.ReadAllTextAsync(fixture.ManifestPath, TestContext.Current.CancellationToken)).ShouldBe(original);
        launcher.StartCalls.ShouldBe(initiallyReachable ? 0 : 1);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(503)]
    public async Task ExecuteAsync_StartRejected_PreservesExistingState(int status)
    {
        using var fixture = new DataTransferFixture();
        await WriteManifestAsync(fixture);
        var statePath = WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath).ShouldNotBeNull());
        await File.WriteAllTextAsync(statePath, "previous-workspace-id", TestContext.Current.CancellationToken);
        var launcher = new RecordingLauncher();
        fixture.Respond = path => path switch
        {
            "/health" => DataTransferFixture.Response(200),
            "/api/workspaces" => DataTransferFixture.Response(status, """{"detail":"conflict-marker"}"""),
            _ => throw new InvalidOperationException(path)
        };

        var result = await Command(fixture, launcher).ExecuteAsync(
            new WorkspaceUpOptions("source", "local", null), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Requests.Select(request => request.Path).ShouldBe(["/health", "/api/workspaces"]);
        (await File.ReadAllTextAsync(statePath, TestContext.Current.CancellationToken)).ShouldBe("previous-workspace-id");
        launcher.StartCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_AutoStartFails_DoesNotSubmitWorkspaceOrWriteState()
    {
        using var fixture = new DataTransferFixture();
        await WriteManifestAsync(fixture);
        fixture.Respond = path => path == "/health" ? DataTransferFixture.Response(503)
            : throw new InvalidOperationException(path);
        var launcher = new RecordingLauncher { Start = _ => Task.FromResult(false) };

        var result = await Command(fixture, launcher).ExecuteAsync(
            new WorkspaceUpOptions("source", "local", null), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        launcher.StartCalls.ShouldBe(1);
        fixture.Requests.ShouldHaveSingleItem().Path.ShouldBe("/health");
        File.Exists(WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("health")]
    [InlineData("launch")]
    [InlineData("start")]
    public async Task ExecuteAsync_CancelledDuringStartup_DoesNotWriteRunningState(string phase)
    {
        using var fixture = new DataTransferFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await WriteManifestAsync(fixture);
        var launcher = new RecordingLauncher
        {
            Start = token =>
            {
                token.ShouldBe(cancellation.Token);
                cancellation.Cancel();
                return Task.FromResult(false);
            }
        };
        fixture.Respond = path =>
        {
            if (path == "/health" && phase != "health")
                return DataTransferFixture.Response(phase == "launch" ? 503 : 200);
            cancellation.Cancel();
            throw new TaskCanceledException("cancelled startup probe", null, cancellation.Token);
        };

        var result = await Command(fixture, launcher).ExecuteAsync(
            new WorkspaceUpOptions("source", "local", null), cancellation.Token);

        result.ShouldBe(130);
        File.Exists(WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath)).ShouldBeFalse();
        launcher.StartCalls.ShouldBe(phase == "launch" ? 1 : 0);
        fixture.Requests.Select(request => request.Path).ShouldBe(phase == "start"
            ? ["/health", "/api/workspaces"] : ["/health"]);
    }

    [Theory]
    [InlineData("  \n")]
    [InlineData("invalid token")]
    public async Task ExecuteAsync_InvalidCapability_DoesNotSubmitWorkspace(string token)
    {
        using var fixture = new DataTransferFixture();
        await WriteManifestAsync(fixture);
        var capability = Path.Join(fixture.Root, "capability.txt");
        await File.WriteAllTextAsync(capability, token, TestContext.Current.CancellationToken);
        fixture.Respond = path => path == "/health" ? DataTransferFixture.Response(200)
            : throw new InvalidOperationException(path);
        var launcher = new RecordingLauncher();

        var result = await Command(fixture, launcher).ExecuteAsync(
            new WorkspaceUpOptions("source", "local", capability), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Requests.ShouldNotContain(request => request.Path == "/api/workspaces");
        File.Exists(WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath)).ShouldBeFalse();
        launcher.StartCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_MissingManifest_DoesNotContactServerOrLaunch()
    {
        using var fixture = new DataTransferFixture();
        fixture.Resolver.Path = null;
        var launcher = new RecordingLauncher();

        var result = await Command(fixture, launcher).ExecuteAsync(
            new WorkspaceUpOptions("missing", "local", null), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Requests.ShouldBeEmpty();
        launcher.StartCalls.ShouldBe(0);
    }

    private static Task WriteManifestAsync(DataTransferFixture fixture) => File.WriteAllTextAsync(fixture.ManifestPath,
        """{"version":"1.0","name":"source","agents":{"assistant":{"model":"model-marker","system_prompt_file":"./prompts/assistant.md","tools":["files"],"capabilities":["tool:files:invoke:read"]}},"tools":{"files":{"type":"filesystem"}}}""",
        TestContext.Current.CancellationToken);

    private static HttpResponseMessage Started() => DataTransferFixture.Response(201,
        """{"workspaceId":"new-workspace-id","name":"source","status":"Running","recoveryCondition":"StartedOnThisHost","containerCount":0}""");

    private static WorkspaceUpCliCommand Command(DataTransferFixture fixture, ISiloLauncher launcher)
    {
        var config = Substitute.For<ISystemConfigSource>();
        config.Load().Returns(new SystemConfigSnapshot
        {
            Version = "1.0",
            BaseUrl = "https://example.test",
            DefaultPort = 9401,
            Storage = "memory",
            AuthMode = "none",
            RequireHttps = true,
            WeaveHome = fixture.Root,
            SiloPath = null
        });
        return new WorkspaceUpCliCommand(new StartWorkspaceAction(fixture.Client), new GetSystemInfoAction(config, fixture.Client),
            fixture.Resolver, new WorkspacePrompt(fixture.Registry, fixture.Resolver), launcher);
    }

    private sealed class RecordingLauncher : ISiloLauncher
    {
        public int StartCalls { get; private set; }
        public Func<CancellationToken, Task<bool>> Start { get; init; } =
            _ => throw new InvalidOperationException("Unexpected server launch.");

        public Task<bool> AutoStartServeAsync(CancellationToken ct)
        {
            StartCalls++;
            return Start(ct);
        }

        public string? ResolveSiloPath() => throw new InvalidOperationException("Unexpected path lookup.");
        public Task<SiloAutoStartResult> AutoStartServeWithDiagnosticsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected diagnostic launch.");
    }
}
