using System.Net;
using Weave.Actions.Agent;
using Weave.Actions.Config;
using Weave.Actions.SystemInfo;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

[Collection("Tui view console")]
public sealed class TuiUtilityViewTests
{
    [Fact]
    public async Task DispatchAsync_Config_RendersLocalSettingsAndEditHintWithoutHttp()
    {
        using var context = new TuiViewTestContext();
        var view = new TuiConfigView(new GetConfigAction(new TuiViewTestContext.ConfigSource()));

        await view.DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("CLI Configuration");
        context.Output.ShouldContain("9400");
        context.Output.ShouldContain("https://silo.test");
        context.Output.ShouldContain("(not set)");
        context.Output.ShouldContain("Change settings with: weave config set <key> <value>");
        context.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "online")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "offline")]
    public async Task DispatchAsync_System_CombinesConfigAndHealth(HttpStatusCode status, string expected)
    {
        using var context = new TuiViewTestContext();
        context.Respond = _ => TuiViewTestContext.Json("{}", status);
        var view = new TuiSystemView(new GetSystemInfoAction(new TuiViewTestContext.ConfigSource(), context.Client));

        await view.DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain(expected + " · https://silo.test");
        context.Output.ShouldContain("9400");
        context.Output.ShouldContain("unused-test-home");
        context.Requests.ShouldBe(["/health"]);
    }

    [Fact]
    public async Task DispatchAsync_Refresh_RequeriesDashboardWithoutChangingSession()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        var dashboard = new TuiWorkspaceDashboard(new GetSystemInfoAction(new TuiViewTestContext.ConfigSource(), context.Client),
            new TuiViewTestContext.ReadOnlyRegistry(new Dictionary<string, string>()));

        await new RefreshVerb(dashboard).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("No workspaces registered yet.");
        context.Requests.ShouldBe(["/health"]);
        context.Session.WorkspaceId.ShouldBe("workspace-42");
        context.Session.AgentName.ShouldBe("reviewer");
    }

    [Theory]
    [InlineData(false, "No workspace open. Try: /open <workspace>")]
    [InlineData(true, "Workspace 'review-space' is not running.")]
    public async Task DispatchAsync_WatchWithoutRunningWorkspace_ShowsHintWithoutHttp(bool open, string expected)
    {
        using var context = new TuiViewTestContext();
        if (open)
            context.Open();
        var action = new WatchWorkspaceAction(new GetWorkspaceStatusAction(context.Client),
            new ListAgentsAction(context.Client), new ListToolsAction(context.Client));
        var watcher = new TuiWorkspaceWatcher(new TuiLiveStatusView(action, new TuiLiveStatusWatcher(action)));

        await watcher.DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain(expected);
        context.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAsync_WatchUnreadableManifest_ReportsErrorWithoutStartingLiveDisplay(bool missing)
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        if (missing)
            File.Delete(context.ManifestPath);
        else
            await File.WriteAllTextAsync(context.ManifestPath, "{ invalid", TestContext.Current.CancellationToken);
        var action = new WatchWorkspaceAction(new GetWorkspaceStatusAction(context.Client),
            new ListAgentsAction(context.Client), new ListToolsAction(context.Client));
        var watcher = new TuiWorkspaceWatcher(new TuiLiveStatusView(action, new TuiLiveStatusWatcher(action)));

        await watcher.DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        if (missing)
        {
            context.Output.ShouldContain(CliTheme.IconError);
            context.Output.ShouldContain(context.ManifestPath);
        }
        else
            context.Output.ShouldContain("Failed to parse manifest:");
        context.Requests.ShouldBeEmpty();
        context.Session.WorkspaceId.ShouldBe("workspace-42");
    }
}
