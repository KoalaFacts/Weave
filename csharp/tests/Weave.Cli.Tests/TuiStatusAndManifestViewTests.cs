using System.Net;
using Spectre.Console;
using Weave.Actions.Agent;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Collection("Tui view console")]
public sealed class TuiStatusAndManifestViewTests
{
    [Fact]
    public void Render_ManifestWithAgentsAndTools_PreservesLiteralMarkupAndRelationships()
    {
        using var context = new TuiViewTestContext();
        var manifest = new ManifestParser().Parse("""
            {"version":"1.0","workspace":{"isolation":"full"},"name":"review[space]","agents":{"review[agent]":{"model":"model[local]","tools":["files[local]","search"]}},"tools":{"files[local]":{"type":"filesystem"},"search":{"type":"direct_http"}}}
            """);

        TuiManifestView.Render(manifest, context.ManifestPath);

        context.Output.ShouldContain("review[space]");
        context.Output.ShouldContain("1.0");
        context.Output.ShouldContain("Full");
        context.Output.ShouldContain(context.ManifestPath);
        context.Output.ShouldContain("Agents (from manifest)");
        context.Output.ShouldContain("review[agent]");
        context.Output.ShouldContain("model[local]");
        context.Output.ShouldContain("files[local], search");
        context.Output.ShouldContain("Tools (from manifest)");
        context.Output.ShouldContain("filesystem");
        context.Output.ShouldContain("direct_http");
    }

    [Fact]
    public void Render_ManifestWithoutAgentsOrTools_OmitsEmptyTables()
    {
        using var context = new TuiViewTestContext();
        var manifest = new WorkspaceManifest { Name = "empty-workspace", Version = "1.0" };

        TuiManifestView.Render(manifest, context.ManifestPath);

        context.Output.ShouldContain("empty-workspace");
        context.Output.ShouldContain(context.ManifestPath);
        context.Output.ShouldNotContain("Agents (from manifest)");
        context.Output.ShouldNotContain("Tools (from manifest)");
    }

    [Fact]
    public void Build_LiveSnapshot_SortsRowsAndShowsRecoveryCountsAndLiteralNames()
    {
        using var context = new TuiViewTestContext();
        var manifest = new WorkspaceManifest { Name = "workspace[local]", Version = "1.0" };
        var startedAt = new DateTimeOffset(2026, 10, 1, 10, 20, 0, TimeSpan.Zero);
        var workspace = new WorkspaceStatusSummary
        {
            WorkspaceId = "workspace-42",
            Status = "Running",
            ContainerCount = 7,
            RecoveryCondition = WorkspaceRecoveryCondition.RequiresReconciliation,
            StartedAt = startedAt
        };
        AgentSummary[] agents =
        [
            new() { AgentName = "zeta-agent", Status = "Idle", Model = null, ActiveTasksCount = 0, ConnectedToolsCount = 0 },
            new() { AgentName = "alpha[agent]", Status = "Running", Model = "model[local]", ActiveTasksCount = 12, ConnectedToolsCount = 34 }
        ];
        ToolSummary[] tools =
        [
            new() { ToolName = "zeta-tool", ToolType = "http", Status = "Disconnected" },
            new() { ToolName = "alpha[tool]", ToolType = "filesystem", Status = "Connected" }
        ];

        AnsiConsole.Write(TuiLiveStatusRenderer.Build(context.ManifestPath, manifest, workspace, agents, tools));

        context.Output.ShouldContain("workspace[local]");
        context.Output.ShouldContain("workspace-42");
        context.Output.ShouldContain("Needs review before use");
        context.Output.ShouldContain(startedAt.ToLocalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        context.Output.ShouldContain(context.ManifestPath);
        var containerRow = context.Output.Split('\n').Single(line => line.Contains("Containers", StringComparison.Ordinal));
        containerRow.ShouldContain("7");
        var agentRow = context.Output.Split('\n').Single(line => line.Contains("alpha[agent]", StringComparison.Ordinal));
        agentRow.ShouldContain("model[local]");
        agentRow.ShouldContain("12");
        agentRow.ShouldContain("34");
        context.Output.ShouldContain("zeta-agent");
        context.Output.ShouldContain("alpha[tool]");
        context.Output.ShouldContain("zeta-tool");
        context.Output.IndexOf("alpha[agent]", StringComparison.Ordinal)
            .ShouldBeLessThan(context.Output.IndexOf("zeta-agent", StringComparison.Ordinal));
        context.Output.IndexOf("alpha[tool]", StringComparison.Ordinal)
            .ShouldBeLessThan(context.Output.IndexOf("zeta-tool", StringComparison.Ordinal));
        context.Output.ShouldContain("filesystem");
        context.Output.ShouldContain("Disconnected");
    }

    [Fact]
    public void Build_EmptySnapshot_OmitsAgentToolRecoveryAndStartedSections()
    {
        using var context = new TuiViewTestContext();
        var workspace = new WorkspaceStatusSummary
        {
            WorkspaceId = "workspace-42",
            Status = "Stopped",
            ContainerCount = 0,
            RecoveryCondition = WorkspaceRecoveryCondition.StartedOnThisHost
        };

        AnsiConsole.Write(TuiLiveStatusRenderer.Build(context.ManifestPath,
            new WorkspaceManifest { Name = "empty-workspace", Version = "1.0" }, workspace, [], []));

        context.Output.ShouldContain("empty-workspace");
        context.Output.ShouldContain("Stopped");
        context.Output.ShouldNotContain("Active Tasks");
        context.Output.ShouldNotContain("Tools");
        context.Output.ShouldNotContain("Recovery");
        context.Output.ShouldNotContain("Started");
    }

    [Fact]
    public async Task TryRenderOnceAsync_NoStateFile_ReturnsFalseWithoutHttpOrOutput()
    {
        using var context = new TuiViewTestContext();

        var rendered = await View(context).TryRenderOnceAsync(context.ManifestPath, Manifest(), TestContext.Current.CancellationToken);

        rendered.ShouldBeFalse();
        context.Requests.ShouldBeEmpty();
        context.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryRenderOnceAsync_StoppedServerWorkspace_ExplainsManifestFallback()
    {
        using var context = new TuiViewTestContext();
        TuiViewTestContext.WriteState(context.ManifestPath);
        context.Respond = _ => TuiViewTestContext.Json("{}", HttpStatusCode.NotFound);

        var rendered = await View(context).TryRenderOnceAsync(context.ManifestPath, Manifest(), TestContext.Current.CancellationToken);

        rendered.ShouldBeFalse();
        context.Output.ShouldContain("Live status unavailable:");
        context.Output.ShouldContain("Workspace 'workspace-42' is not running.");
        context.Output.ShouldContain("Showing manifest instead.");
        context.Requests.ShouldBe(["/api/workspaces/workspace-42"]);
    }

    [Fact]
    public async Task TryRenderOnceAsync_CancelledStatusRequest_ReturnsFalseWithoutWarning()
    {
        using var context = new TuiViewTestContext();
        TuiViewTestContext.WriteState(context.ManifestPath);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var rendered = await View(context).TryRenderOnceAsync(context.ManifestPath, Manifest(), cancellation.Token);

        rendered.ShouldBeFalse();
        context.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryRenderOnceAsync_LiveWorkspace_UsesDiskIdentityAndRendersCompositeSnapshot()
    {
        using var context = new TuiViewTestContext();
        TuiViewTestContext.WriteState(context.ManifestPath, "disk-workspace");
        context.Respond = path => path switch
        {
            "/api/workspaces/disk-workspace" => TuiViewTestContext.Json("""
                {"workspaceId":"disk-workspace","name":"server-space","status":"Running","recoveryCondition":"StartedOnThisHost","containerCount":2}
                """),
            "/api/workspaces/disk-workspace/agents" => TuiViewTestContext.Json("""
                [{"agentName":"live-reviewer","status":"Running","model":"live-model"}]
                """),
            "/api/workspaces/disk-workspace/tools" => TuiViewTestContext.Json("""
                [{"toolName":"live-files","toolType":"filesystem","status":"Connected"}]
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {path}")
        };

        var rendered = await View(context).TryRenderOnceAsync(context.ManifestPath, Manifest(), TestContext.Current.CancellationToken);

        rendered.ShouldBeTrue();
        context.Output.ShouldContain("disk-workspace");
        context.Output.ShouldContain("live-reviewer");
        context.Output.ShouldContain("live-model");
        context.Output.ShouldContain("live-files");
        context.Output.ShouldContain("Connected");
        context.Output.ShouldNotContain("Showing manifest instead");
        context.Requests.ShouldBe([
            "/api/workspaces/disk-workspace",
            "/api/workspaces/disk-workspace/agents",
            "/api/workspaces/disk-workspace/tools"]);
    }

    [Fact]
    public async Task WatchAsync_NoStateFile_ShowsNothingToWatchWithoutHttpRequest()
    {
        using var context = new TuiViewTestContext();

        await View(context).WatchAsync(context.ManifestPath, Manifest(), TestContext.Current.CancellationToken);

        context.Output.ShouldContain("No workspace ID on disk — nothing to watch.");
        context.Requests.ShouldBeEmpty();
    }

    private static WorkspaceManifest Manifest() => new ManifestParser().Parse(TuiViewTestContext.ManifestJson);

    private static TuiLiveStatusView View(TuiViewTestContext context)
    {
        var watch = new WatchWorkspaceAction(new GetWorkspaceStatusAction(context.Client),
            new ListAgentsAction(context.Client), new ListToolsAction(context.Client));
        return new TuiLiveStatusView(watch, new TuiLiveStatusWatcher(watch));
    }
}
