using System.Net;

namespace Weave.Silo.Tests.Dashboard;

public sealed class DashboardAgentRenderingTests
{
    [Fact]
    public async Task RenderAgents_MultipleWorkspaces_DisplaysEachAgentWithoutSelectingOne()
    {
        var replies = new Dictionary<string, string>
        {
            ["/api/workspaces"] = """[{"workspaceId":"release-lab"},{"workspaceId":"docs-lab"}]""",
            ["/api/workspaces/release-lab/agents"] = """
                [{"workspaceId":"release-lab","agentName":"release-reviewer","status":"Active","model":"review-model"}]
                """,
            ["/api/workspaces/docs-lab/agents"] = """
                [{"workspaceId":"docs-lab","agentName":"docs-writer","status":"Idle","connectedTools":["docs"]}]
                """
        };
        await using var fixture = new DashboardPageFixture((request, _) =>
        {
            request.Method.ShouldBe(HttpMethod.Get);
            var route = request.RequestUri.ShouldNotBeNull().PathAndQuery;
            replies.TryGetValue(route, out var json).ShouldBeTrue("Unexpected request: " + route);
            return Task.FromResult(DashboardPageFixture.Json(json.ShouldNotBeNull()));
        });

        var html = await fixture.RenderAsync("Agents");

        var text = DashboardPageFixture.Text(html);
        text.ShouldContain("release-reviewer (Active)");
        text.ShouldContain("docs-writer (Idle)");
        text.ShouldContain("Select an agent");
        text.ShouldNotContain("No agents active");
        html.ShouldNotContain("<fluent-progress-ring");
        html.ShouldContain("disabled");
    }

    [Fact]
    public async Task RenderAgents_NoWorkspaces_DisplaysEmptyConsole()
    {
        await using var fixture = new DashboardPageFixture((_, _) => Task.FromResult(DashboardPageFixture.Json("[]")));

        var html = await fixture.RenderAsync("Agents");

        var text = DashboardPageFixture.Text(html);
        text.ShouldContain("No agents active");
        text.ShouldContain("Select an agent");
        text.ShouldNotContain("Unable to connect");
        html.ShouldNotContain("<fluent-progress-ring");
    }

    [Fact]
    public async Task RenderAgents_AgentRequestFails_ShowsSafeConnectionErrorAndFinishesLoading()
    {
        await using var fixture = new DashboardPageFixture((request, _) => Task.FromResult(
            request.RequestUri.ShouldNotBeNull().AbsolutePath == "/api/workspaces"
                ? DashboardPageFixture.Json("""[{"workspaceId":"release-lab"}]""")
                : DashboardPageFixture.Json("private-upstream-marker", HttpStatusCode.ServiceUnavailable)));

        var html = await fixture.RenderAsync("Agents");

        DashboardPageFixture.Text(html).ShouldContain("Unable to connect to the Weave Silo API.");
        html.ShouldNotContain("private-upstream-marker");
        html.ShouldNotContain("<fluent-progress-ring");
    }

    [Fact]
    public async Task RenderAgents_PendingResponse_ReplacesLoadingIndicatorWithAgent()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new DashboardPageFixture((request, _) =>
            request.RequestUri.ShouldNotBeNull().AbsolutePath == "/api/workspaces"
                ? Task.FromResult(DashboardPageFixture.Json("""[{"workspaceId":"release-lab"}]"""))
                : reply.Task);
        var root = await fixture.BeginAsync("Agents");
        string initial;
        try
        {
            initial = await fixture.HtmlAsync(root);
        }
        finally
        {
            reply.TrySetResult(DashboardPageFixture.Json("""[{"workspaceId":"release-lab","agentName":"release-reviewer","status":"Active"}]"""));
        }
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var html = await fixture.HtmlAsync(root);

        initial.ShouldContain("<fluent-progress-ring");
        html.ShouldNotContain("<fluent-progress-ring");
        DashboardPageFixture.Text(html).ShouldContain("release-reviewer (Active)");
    }
}
