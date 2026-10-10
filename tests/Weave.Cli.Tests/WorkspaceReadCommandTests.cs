using Weave.Actions.Agent;
using Weave.Actions.AgentTask;
using Weave.Actions.Tool;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class WorkspaceReadCommandTests
{
    private const string Manifest = """{"version":"1.0","name":"read-workspace","agents":{"manifest-agent":{"model":"manifest-model","capabilities":["tool:manifest-tool:invoke:read"]}},"tools":{"manifest-tool":{"type":"filesystem"}}}""";
    private const string State = "  live-workspace\n";

    [Theory]
    [InlineData("agents", "live [agent]")]
    [InlineData("tools", "live [tool]")]
    [InlineData("tasks", "task [description]")]
    public async Task ExecuteAsync_LiveData_RendersServerResultsAndPreservesFiles(string command, string expected)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new DataTransferFixture();
        await PrepareAsync(fixture, true);
        fixture.Respond = _ => DataTransferFixture.Response(200, command switch
        {
            "agents" => """[{"agentName":"live [agent]","status":"Running","model":"live-model","connectedTools":["one"],"activeTasks":[{}]}]""",
            "tools" => """[{"toolName":"live [tool]","toolType":"mcp","status":"Connected","endpoint":"https://tools.test/[endpoint]"}]""",
            "tasks" => """[{"taskId":"task-id","description":"task [description]","status":"Completed","createdAt":"2026-01-02T03:04:05Z"}]""",
            _ => throw new ArgumentException("Unknown command.", nameof(command))
        });

        var result = await ExecuteAsync(command, fixture, TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Requests.ShouldHaveSingleItem().Path.ShouldBe(Endpoint(command));
        fixture.Requests.ShouldAllBe(request => request.Method == "GET");
        output.Text.ShouldContain(expected);
        if (command != "tasks")
            output.Text.ShouldNotContain("manifest-" + (command == "agents" ? "agent" : "tool"));
        await AssertPreservedAsync(fixture);
    }

    [Theory]
    [InlineData("agents", "No live agents reported by the silo.")]
    [InlineData("tools", "No live tools reported by the silo.")]
    [InlineData("tasks", "No tasks for agent 'manifest-agent'.")]
    public async Task ExecuteAsync_EmptyLiveResults_DoesNotRenderManifestAsLive(string command, string message)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new DataTransferFixture();
        await PrepareAsync(fixture, true);
        fixture.Respond = _ => DataTransferFixture.Response(200, "[]");

        (await ExecuteAsync(command, fixture, TestContext.Current.CancellationToken)).ShouldBe(0);

        output.Text.ShouldContain(message);
        fixture.Requests.ShouldHaveSingleItem().Path.ShouldBe(Endpoint(command));
        if (command != "tasks")
            output.Text.ShouldNotContain("manifest-" + (command == "agents" ? "agent" : "tool"));
        await AssertPreservedAsync(fixture);
    }

    [Theory]
    [InlineData("agents", "manifest-agent")]
    [InlineData("tools", "manifest-tool")]
    [InlineData("tasks", "Workspace is not running.")]
    public async Task ExecuteAsync_WithoutState_UsesOfflineBehaviorWithoutHttp(string command, string message)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new DataTransferFixture();
        await PrepareAsync(fixture, false);

        (await ExecuteAsync(command, fixture, TestContext.Current.CancellationToken)).ShouldBe(0);

        output.Text.ShouldContain(message);
        fixture.Requests.ShouldBeEmpty();
        (await File.ReadAllTextAsync(fixture.ManifestPath, TestContext.Current.CancellationToken)).ShouldBe(Manifest);
        File.Exists(WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("agents", 0, "manifest-agent")]
    [InlineData("tools", 0, "manifest-tool")]
    [InlineData("tasks", 1, "Silo unreachable:")]
    public async Task ExecuteAsync_UnreachableServer_UsesDocumentedFallbackWithoutChangingState(string command, int exitCode, string message)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new DataTransferFixture();
        await PrepareAsync(fixture, true);
        fixture.Respond = _ => throw new HttpRequestException("read-query-marker");

        (await ExecuteAsync(command, fixture, TestContext.Current.CancellationToken)).ShouldBe(exitCode);

        output.Text.ShouldContain("read-query-marker");
        output.Text.ShouldContain(message);
        fixture.Requests.ShouldHaveSingleItem().Path.ShouldBe(Endpoint(command));
        await AssertPreservedAsync(fixture);
    }

    [Theory]
    [InlineData("agents")]
    [InlineData("tools")]
    [InlineData("tasks")]
    public async Task ExecuteAsync_QueryCancelled_ReturnsCancellationAndPreservesState(string command)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new DataTransferFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await PrepareAsync(fixture, true);
        fixture.Respond = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        (await ExecuteAsync(command, fixture, cancellation.Token)).ShouldBe(130);

        fixture.Requests.ShouldHaveSingleItem().Path.ShouldBe(Endpoint(command));
        output.Text.ShouldBeEmpty();
        await AssertPreservedAsync(fixture);
    }

    [Theory]
    [InlineData("agents")]
    [InlineData("tools")]
    [InlineData("tasks")]
    public async Task ExecuteAsync_MissingManifest_DoesNotQueryServer(string command)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new DataTransferFixture();
        fixture.Resolver.Path = null;

        (await ExecuteAsync(command, fixture, TestContext.Current.CancellationToken)).ShouldBe(1);

        fixture.Requests.ShouldBeEmpty();
        output.Text.ShouldContain("No workspace.json found");
    }

    [Theory]
    [InlineData("agents", 0, "No agents declared")]
    [InlineData("tools", 0, "No tools declared")]
    [InlineData("tasks", 1, "No agents declared")]
    public async Task ExecuteAsync_EmptyOfflineManifest_ReportsMissingEntries(string command, int exitCode, string message)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new DataTransferFixture();
        await File.WriteAllTextAsync(fixture.ManifestPath, """{"version":"1.0","name":"empty"}""", TestContext.Current.CancellationToken);

        (await ExecuteAsync(command, fixture, TestContext.Current.CancellationToken)).ShouldBe(exitCode);

        fixture.Requests.ShouldBeEmpty();
        output.Text.ShouldContain(message);
    }

    private static async Task PrepareAsync(DataTransferFixture fixture, bool writeState)
    {
        await File.WriteAllTextAsync(fixture.ManifestPath, Manifest, TestContext.Current.CancellationToken);
        if (writeState)
        {
            var state = WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(state).ShouldNotBeNull());
            await File.WriteAllTextAsync(state, State, TestContext.Current.CancellationToken);
        }
    }

    private static async Task AssertPreservedAsync(DataTransferFixture fixture)
    {
        (await File.ReadAllTextAsync(fixture.ManifestPath, TestContext.Current.CancellationToken)).ShouldBe(Manifest);
        (await File.ReadAllTextAsync(WorkspaceManifestPaths.GetStatePath(fixture.ManifestPath),
            TestContext.Current.CancellationToken)).ShouldBe(State);
    }

    private static string Endpoint(string command) => command == "tasks"
        ? "/api/workspaces/live-workspace/agents/manifest-agent/tasks" : "/api/workspaces/live-workspace/" + command;

    private static Task<int> ExecuteAsync(string command, DataTransferFixture fixture, CancellationToken ct)
    {
        var prompt = new WorkspacePrompt(fixture.Registry, fixture.Resolver);
        return command switch
        {
            "agents" => new AgentsCliCommand(new ListAgentsAction(fixture.Client), fixture.Resolver, prompt)
                .ExecuteAsync(new WorkspaceNameOptions("read-workspace"), ct),
            "tools" => new ToolsCliCommand(new ListToolsAction(fixture.Client), fixture.Resolver, prompt)
                .ExecuteAsync(new WorkspaceNameOptions("read-workspace"), ct),
            "tasks" => new TasksCliCommand(new ListTasksAction(fixture.Client), fixture.Resolver, prompt)
                .ExecuteAsync(new TasksOptions("read-workspace", null), ct),
            _ => throw new ArgumentException("Unknown command.", nameof(command))
        };
    }
}
