using System.Collections.Concurrent;
using System.Net;

namespace Weave.Silo.Tests.Dashboard;

public sealed class DashboardWorkspaceRenderingTests
{
    [Fact]
    public async Task RenderHome_MultipleWorkspaces_DisplaysAggregateAgentAndToolCounts()
    {
        var responses = new Dictionary<string, string>
        {
            ["/api/workspaces"] = """[{"workspaceId":"release-lab"},{"workspaceId":"docs-lab"}]""",
            ["/api/workspaces/release-lab/agents"] = """[{"agentName":"reviewer"},{"agentName":"builder"}]""",
            ["/api/workspaces/docs-lab/agents"] = """[{"agentName":"writer"}]""",
            ["/api/workspaces/release-lab/tools"] = """[{"toolName":"files"}]""",
            ["/api/workspaces/docs-lab/tools"] = """[{"toolName":"search"},{"toolName":"git"},{"toolName":"docs"}]"""
        };
        await using var fixture = Fixture(responses);

        var html = await fixture.RenderAsync("Home");

        DashboardPageFixture.Text(html).ShouldContain("Workspaces 2 Active Agents 3 Running Tools 4 Connected");
        html.ShouldContain("href=\"/workspaces\"");
        html.ShouldNotContain("Unable to connect");
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("Workspaces")]
    [InlineData("WorkspaceDetail")]
    [InlineData("Tools")]
    public async Task RenderPage_ApiUnavailable_ShowsConnectionErrorWithoutLoadingIndicator(string page)
    {
        await using var fixture = new DashboardPageFixture((_, _) =>
            Task.FromResult(DashboardPageFixture.Json("upstream-private-marker", HttpStatusCode.ServiceUnavailable)));

        var html = await fixture.RenderAsync(page, page == "WorkspaceDetail" ? new() { ["WorkspaceId"] = "release-lab" } : null);

        DashboardPageFixture.Text(html).ShouldContain("Unable to connect to the Weave Silo API.");
        html.ShouldNotContain("upstream-private-marker");
        html.ShouldNotContain("<fluent-progress-ring");
    }

    [Fact]
    public async Task RenderWorkspaces_NamedAndUnnamedWorkspaces_DisplaysDetailLinksAndLifecycleData()
    {
        await using var fixture = Fixture(new()
        {
            ["/api/workspaces"] = """
                [{"workspaceId":"release-lab","name":"Release laboratory","status":"Running","containerCount":7},
                 {"workspaceId":"archived-lab","status":"Stopped","containerCount":0},
                 {"workspaceId":"broken-lab","name":"Broken laboratory","status":"Error","containerCount":1},
                 {"workspaceId":"pending-lab","status":"Pending","containerCount":0}]
                """
        });

        var html = await fixture.RenderAsync("Workspaces");

        var text = DashboardPageFixture.Text(html);
        text.ShouldContain("Release laboratory");
        text.ShouldContain("Running");
        text.ShouldContain("archived-lab");
        text.ShouldContain("Stopped");
        text.ShouldContain("Broken laboratory");
        text.ShouldContain("Error");
        text.ShouldContain("Pending");
        html.ShouldContain("href=\"/workspaces/release-lab\"");
        html.ShouldContain("href=\"/workspaces/archived-lab\"");
        html.ShouldNotContain("<fluent-progress-ring");
    }

    [Theory]
    [InlineData("Workspaces")]
    [InlineData("Tools")]
    public async Task RenderPage_PendingWorkspaceRequest_ShowsLoadingUntilResponseArrives(string page)
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new DashboardPageFixture((_, _) => response.Task);
        var root = await fixture.BeginAsync(page);
        string initial;
        try
        {
            initial = await fixture.HtmlAsync(root);
        }
        finally
        {
            response.TrySetResult(DashboardPageFixture.Json("[]"));
        }
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var loaded = await fixture.HtmlAsync(root);

        initial.ShouldContain("<fluent-progress-ring");
        loaded.ShouldNotContain("<fluent-progress-ring");
        loaded.ShouldNotContain("Unable to connect");
        if (page == "Tools")
            DashboardPageFixture.Text(loaded).ShouldContain("No tools connected. Start a workspace to register tools.");
        else
            DashboardPageFixture.Text(loaded).ShouldContain("Workspace ID");
    }

    [Fact]
    public async Task RenderWorkspaceDetail_WorkspaceAndConnections_ShowsOwnedMetadataAndRows()
    {
        await using var fixture = Fixture(new()
        {
            ["/api/workspaces/release-lab"] = """
                {"workspaceId":"release-lab","name":"Release laboratory","status":"Error","containerCount":7,
                 "startedAt":"2030-01-01T08:00:00Z","stoppedAt":"2030-01-01T09:00:00Z","errorMessage":"container-exit-marker"}
                """,
            ["/api/workspaces/release-lab/agents"] = """
                [{"agentName":"release-reviewer","status":"Active","model":"review-model","connectedTools":["files","git"]},
                 {"agentName":"idle-reviewer","connectedTools":[],"status":"Idle"},{"agentName":"broken-reviewer","connectedTools":[],"status":"Error"},
                 {"agentName":"pending-reviewer","connectedTools":[],"status":"Pending"}]
                """,
            ["/api/workspaces/release-lab/tools"] = """
                [{"toolName":"release-files","toolType":"FileSystem","status":"Connected","endpoint":"/controlled/release"},
                 {"toolName":"offline-files","toolType":"FileSystem","status":"Disconnected"},
                 {"toolName":"broken-files","toolType":"FileSystem","status":"Error"},
                 {"toolName":"pending-files","toolType":"FileSystem","status":"Pending"}]
                """
        });

        var html = await fixture.RenderAsync("WorkspaceDetail", new() { ["WorkspaceId"] = "release-lab" });

        var text = DashboardPageFixture.Text(html);
        text.ShouldContain("Release laboratory");
        text.ShouldContain("Workspace ID: release-lab");
        text.ShouldContain("Containers: 7");
        text.ShouldContain("Started:");
        text.ShouldContain("Stopped:");
        text.ShouldContain("Error: container-exit-marker");
        text.ShouldContain("release-reviewer");
        text.ShouldContain("review-model");
        text.ShouldContain("files, git");
        text.ShouldContain("idle-reviewer");
        text.ShouldContain("broken-reviewer");
        text.ShouldContain("pending-reviewer");
        text.ShouldContain("release-files");
        text.ShouldContain("/controlled/release");
        text.ShouldContain("Disconnected");
        text.ShouldContain("pending-files");
        html.ShouldNotContain("No agents active.");
        html.ShouldNotContain("No tools connected.");
    }

    [Fact]
    public async Task RenderWorkspaceDetail_NoConnections_DisplaysWorkspaceAndExplicitEmptyStates()
    {
        await using var fixture = Fixture(new()
        {
            ["/api/workspaces/empty-lab"] = """{"workspaceId":"empty-lab","status":"Running"}""",
            ["/api/workspaces/empty-lab/agents"] = "[]",
            ["/api/workspaces/empty-lab/tools"] = "[]"
        });

        var html = await fixture.RenderAsync("WorkspaceDetail", new() { ["WorkspaceId"] = "empty-lab" });

        var text = DashboardPageFixture.Text(html);
        text.ShouldContain("Workspace ID: empty-lab");
        text.ShouldContain("Status: Running");
        text.ShouldContain("No agents active.");
        text.ShouldContain("No tools connected.");
        text.ShouldNotContain("Started:");
        text.ShouldNotContain("Stopped:");
        html.ShouldNotContain("<fluent-progress-ring");
    }

    [Fact]
    public async Task RenderWorkspaceDetail_NullWorkspace_ShowsNotFoundWithoutFetchingConnections()
    {
        var requests = new ConcurrentQueue<string>();
        await using var fixture = new DashboardPageFixture((request, _) =>
        {
            requests.Enqueue(request.RequestUri.ShouldNotBeNull().PathAndQuery);
            return Task.FromResult(DashboardPageFixture.Json("null"));
        });

        var html = await fixture.RenderAsync("WorkspaceDetail", new() { ["WorkspaceId"] = "missing-lab" });

        DashboardPageFixture.Text(html).ShouldContain("Workspace 'missing-lab' not found.");
        requests.ShouldBe(["/api/workspaces/missing-lab"]);
        html.ShouldNotContain("<fluent-progress-ring");
        html.ShouldNotContain("No agents active.");
    }

    [Fact]
    public async Task RenderTools_MultipleWorkspaces_DisplaysConnectionsFromEachWorkspace()
    {
        await using var fixture = Fixture(new()
        {
            ["/api/workspaces"] = """[{"workspaceId":"release-lab"},{"workspaceId":"docs-lab"}]""",
            ["/api/workspaces/release-lab/tools"] = """
                [{"toolName":"release-files","toolType":"FileSystem","status":"Connected","endpoint":"/controlled/release"},
                 {"toolName":"offline-search","toolType":"Search","status":"Disconnected"}]
                """,
            ["/api/workspaces/docs-lab/tools"] = """
                [{"toolName":"docs-git","toolType":"Cli","status":"Error","endpoint":"/controlled/docs"},
                 {"toolName":"pending-search","toolType":"Search","status":"Pending"}]
                """
        });

        var html = await fixture.RenderAsync("Tools");

        var text = DashboardPageFixture.Text(html);
        text.ShouldContain("release-files");
        text.ShouldContain("FileSystem");
        text.ShouldContain("/controlled/release");
        text.ShouldContain("Connected");
        text.ShouldContain("offline-search");
        text.ShouldContain("Disconnected");
        text.ShouldContain("docs-git");
        text.ShouldContain("/controlled/docs");
        text.ShouldContain("Error");
        text.ShouldContain("pending-search");
        text.ShouldContain("Pending");
        html.ShouldNotContain("No tools connected.");
    }

    private static DashboardPageFixture Fixture(Dictionary<string, string> responses) => new((request, _) =>
    {
        request.Method.ShouldBe(HttpMethod.Get);
        var route = request.RequestUri.ShouldNotBeNull().PathAndQuery;
        responses.TryGetValue(route, out var body).ShouldBeTrue("Unexpected Dashboard request: " + route);
        return Task.FromResult(DashboardPageFixture.Json(body.ShouldNotBeNull()));
    });
}
