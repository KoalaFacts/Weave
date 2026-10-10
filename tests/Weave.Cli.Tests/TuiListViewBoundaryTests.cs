using Weave.Actions.Agent;
using Weave.Actions.AgentTask;
using Weave.Actions.Tool;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

[Collection("Tui view console")]
public sealed class TuiListViewBoundaryTests
{
    [Fact]
    public async Task Agents_NoWorkspace_ShowsOpenHintWithoutHttpRequest()
    {
        using var context = new TuiViewTestContext();

        await Agents(context).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("No workspace open. Try: /open <workspace>");
        context.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Agents_LiveResponse_RendersServerAgentAndSelectionHint()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true);
        context.Respond = _ => TuiViewTestContext.Json("""
            [{"agentName":"remote[agent]","status":"Running","model":"server-model","activeTasks":["task-1"],"connectedTools":["files"]}]
            """);

        await Agents(context).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("remote[agent]");
        context.Output.ShouldContain("server-model");
        context.Output.ShouldContain("Running");
        context.Output.ShouldContain("Pick one with: /use <name>");
        context.Output.ShouldNotContain("reviewer");
        context.Requests.ShouldBe(["/api/workspaces/workspace-42/agents"]);
    }

    [Fact]
    public async Task Agents_StoppedWorkspace_RendersManifestAgentsAndSelectedMarker()
    {
        using var context = new TuiViewTestContext();
        context.Open(agent: "reviewer");

        await Agents(context).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("reviewer");
        context.Output.ShouldContain("●");
        context.Output.ShouldNotContain("Pick one");
        context.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Agents_SiloUnavailable_FallsBackToManifestWithExplanation()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true);
        context.Respond = _ => throw new HttpRequestException("offline-marker");

        await Agents(context).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("reviewer");
        context.Output.ShouldContain("offline-marker");
        context.Output.ShouldContain("Falling back to manifest");
        context.Output.ShouldContain("Pick one with: /use <name>");
    }

    [Fact]
    public async Task Agents_ManifestHasNoAgents_ShowsUnavailableMessage()
    {
        using var context = new TuiViewTestContext();
        File.WriteAllText(context.ManifestPath, """{"version":"1.0","name":"empty-space"}""");
        context.Open();

        await Agents(context).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("No agents available.");
        context.Output.ShouldNotContain("Pick one");
        context.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Agents_CancelledRequest_DoesNotRenderManifestFallback()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Agents(context).DispatchAsync(context.VerbContext, cancellation.Token);

        context.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task AgentNames_LiveResponse_UsesServerNamesInsteadOfManifest()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true);
        context.Respond = _ => TuiViewTestContext.Json("""
            [{"agentName":"server-reviewer","status":"Running","model":"live-model"}]
            """);

        var names = await new TuiAgentNameSource(new ListAgentsAction(context.Client))
            .FetchAsync(context.Session, TestContext.Current.CancellationToken);

        names.ShouldBe(["server-reviewer"]);
        context.Requests.ShouldBe(["/api/workspaces/workspace-42/agents"]);
    }

    [Fact]
    public async Task AgentNames_NoOpenWorkspace_ReturnsEmptyWithoutHttpRequest()
    {
        using var context = new TuiViewTestContext();

        var names = await new TuiAgentNameSource(new ListAgentsAction(context.Client))
            .FetchAsync(context.Session, TestContext.Current.CancellationToken);

        names.ShouldBeEmpty();
        context.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AgentNames_InvalidManifest_ReportsReadFailureAndReturnsNoNames()
    {
        using var context = new TuiViewTestContext();
        File.WriteAllText(context.ManifestPath, "{invalid-json");
        context.Open();

        var names = await new TuiAgentNameSource(new ListAgentsAction(context.Client))
            .FetchAsync(context.Session, TestContext.Current.CancellationToken);

        names.ShouldBeEmpty();
        context.Output.ShouldContain("Could not read manifest for agent names");
        context.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("tasks")]
    public async Task Dispatch_StoppedWorkspace_ShowsStartHintWithoutHttpRequest(string view)
    {
        using var context = new TuiViewTestContext();
        context.Open(agent: "reviewer");

        await View(context, view).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("Workspace is not running. Start it with /up first.");
        context.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tasks_NoSelectedAgent_ShowsSelectionHintWithoutHttpRequest()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true);

        await View(context, "tasks").DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("No agent selected. Use /use <agent> first.");
        context.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("tools", "No tools registered in this workspace.")]
    [InlineData("tasks", "No tasks for agent 'reviewer'.")]
    public async Task Dispatch_EmptyServerList_ShowsSpecificEmptyState(string view, string expected)
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");

        await View(context, view).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain(expected);
        context.Requests.ShouldBe([Endpoint(view)]);
    }

    [Theory]
    [InlineData("tools", "Failed to fetch tools:")]
    [InlineData("tasks", "Failed to fetch tasks:")]
    public async Task Dispatch_TransportFailure_ShowsSpecificFailureAndCause(string view, string expected)
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        context.Respond = _ => throw new HttpRequestException("connection-marker");

        await View(context, view).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain(expected);
        context.Output.ShouldContain("connection-marker");
        context.Output.ShouldNotContain("No tools registered");
        context.Output.ShouldNotContain("No tasks for agent");
        context.Requests.ShouldBe([Endpoint(view)]);
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("tasks")]
    public async Task Dispatch_CancelledRequest_LeavesOutputEmpty(string view)
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await View(context, view).DispatchAsync(context.VerbContext, cancellation.Token);

        context.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tools_LiveResponse_RendersToolDetailsAndEscapesMarkup()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true);
        context.Respond = _ => TuiViewTestContext.Json("""
            [{"toolName":"files[local]","toolType":"FileSystem","status":"Connected","endpoint":"/work/reports"}]
            """);

        await View(context, "tools").DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("files[local]");
        context.Output.ShouldContain("FileSystem");
        context.Output.ShouldContain("Connected");
        context.Output.ShouldContain("/work/reports");
        context.Requests.ShouldBe([Endpoint("tools")]);
    }

    [Fact]
    public async Task Tasks_LiveResponse_RendersSelectedAgentsTaskDetails()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        context.Respond = _ => TuiViewTestContext.Json("""
            [{"taskId":"task-1042","description":"Review [draft] report","status":"Completed","createdAt":"2026-10-01T10:20:00Z"}]
            """);

        await View(context, "tasks").DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("reviewer");
        context.Output.ShouldContain("task-1042");
        context.Output.ShouldContain("Review [draft] report");
        context.Output.ShouldContain("Completed");
        context.Requests.ShouldBe([Endpoint("tasks")]);
    }

    private static TuiAgentListView Agents(TuiViewTestContext context)
    {
        var action = new ListAgentsAction(context.Client);
        return new TuiAgentListView(new TuiAgentNameSource(action), action);
    }

    private static ITuiVerb View(TuiViewTestContext context, string name) => name switch
    {
        "tools" => new TuiToolsView(new ListToolsAction(context.Client)),
        "tasks" => new TuiTasksView(new ListTasksAction(context.Client)),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static string Endpoint(string view) => view == "tools"
        ? "/api/workspaces/workspace-42/tools"
        : "/api/workspaces/workspace-42/agents/reviewer/tasks";
}
