using System.Net;
using System.Text.Json;

namespace Weave.Silo.Tests.Dashboard;

public sealed class DashboardApiClientTests
{
    [Fact]
    public async Task GetWorkspacesAsync_WorkspaceResponse_PreservesIdentityAndLifecycle()
    {
        string? route = null;
        using var http = DashboardPageFixture.Http((request, _) =>
        {
            route = request.Method + " " + request.RequestUri.ShouldNotBeNull().PathAndQuery;
            return Task.FromResult(DashboardPageFixture.Json("""
                [{"workspaceId":"release-lab","name":"Release laboratory","status":"Stopped","containerCount":3,
                  "startedAt":"2030-01-01T08:00:00Z","stoppedAt":"2030-01-01T09:00:00Z","networkId":"isolated-net"}]
                """));
        });

        dynamic rows = await DashboardPageFixture.Api(http).GetWorkspacesAsync(TestContext.Current.CancellationToken);

        route.ShouldBe("GET /api/workspaces");
        ((int)rows.Count).ShouldBe(1);
        ((string)rows[0].WorkspaceId).ShouldBe("release-lab");
        ((string)rows[0].Name).ShouldBe("Release laboratory");
        ((string)rows[0].Status).ShouldBe("Stopped");
        ((int)rows[0].ContainerCount).ShouldBe(3);
        ((DateTimeOffset)rows[0].StoppedAt).ShouldBe(DateTimeOffset.Parse("2030-01-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task GetWorkspaceAsync_SingleWorkspace_UsesWorkspaceRoute()
    {
        string? route = null;
        using var http = DashboardPageFixture.Http((request, _) =>
        {
            route = request.RequestUri.ShouldNotBeNull().PathAndQuery;
            return Task.FromResult(DashboardPageFixture.Json("""{"workspaceId":"release-lab","name":"Release laboratory","status":"Error","errorMessage":"container-start-marker"}"""));
        });

        dynamic workspace = await DashboardPageFixture.Api(http).GetWorkspaceAsync("release-lab", TestContext.Current.CancellationToken);

        route.ShouldBe("/api/workspaces/release-lab");
        ((string)workspace.Name).ShouldBe("Release laboratory");
        ((string)workspace.ErrorMessage).ShouldBe("container-start-marker");
    }

    [Fact]
    public async Task GetAgentsAsync_AgentResponse_PreservesToolsAndActiveTasks()
    {
        string? route = null;
        using var http = DashboardPageFixture.Http((request, _) =>
        {
            route = request.RequestUri.ShouldNotBeNull().PathAndQuery;
            return Task.FromResult(DashboardPageFixture.Json("""
                [{"agentId":"agent-release","workspaceId":"release-lab","agentName":"release-reviewer","status":"Active",
                  "model":"review-model","connectedTools":["files","git"],
                  "activeTasks":[{"taskId":"verify-release","status":"Running","description":"Validate release assets"}]}]
                """));
        });

        dynamic rows = await DashboardPageFixture.Api(http).GetAgentsAsync("release-lab", TestContext.Current.CancellationToken);

        route.ShouldBe("/api/workspaces/release-lab/agents");
        ((int)rows.Count).ShouldBe(1);
        ((string)rows[0].AgentName).ShouldBe("release-reviewer");
        ((string)rows[0].WorkspaceId).ShouldBe("release-lab");
        ((IEnumerable<string>)rows[0].ConnectedTools).ShouldBe(["files", "git"]);
        ((string)rows[0].ActiveTasks[0].TaskId).ShouldBe("verify-release");
    }

    [Fact]
    public async Task GetToolsAsync_ToolResponse_PreservesConnectionDetails()
    {
        string? route = null;
        using var http = DashboardPageFixture.Http((request, _) =>
        {
            route = request.RequestUri.ShouldNotBeNull().PathAndQuery;
            return Task.FromResult(DashboardPageFixture.Json("""
                [{"toolName":"release-files","toolType":"FileSystem","status":"Connected","endpoint":"/controlled/release",
                  "connectedAt":"2030-01-01T08:00:00Z"}]
                """));
        });

        dynamic rows = await DashboardPageFixture.Api(http).GetToolsAsync("release-lab", TestContext.Current.CancellationToken);

        route.ShouldBe("/api/workspaces/release-lab/tools");
        ((int)rows.Count).ShouldBe(1);
        ((string)rows[0].ToolName).ShouldBe("release-files");
        ((string)rows[0].ToolType).ShouldBe("FileSystem");
        ((string)rows[0].Endpoint).ShouldBe("/controlled/release");
        ((string)rows[0].Status).ShouldBe("Connected");
    }

    [Fact]
    public async Task GetRecentCapabilityAuditAsync_CustomLimit_UsesRequestedLimitAndMapsDenial()
    {
        string? route = null;
        using var http = DashboardPageFixture.Http((request, _) =>
        {
            route = request.RequestUri.ShouldNotBeNull().PathAndQuery;
            return Task.FromResult(DashboardPageFixture.Json("""
                [{"tokenId":"review-token","grant":"tool:files:invoke:write_file","issuedTo":"release-reviewer",
                  "workspaceId":"release-lab","actionContext":"write_file","outcome":"Deny","reason":"scope-mismatch-marker",
                  "timestamp":"2030-01-01T08:00:00Z"}]
                """));
        });

        dynamic rows = await DashboardPageFixture.Api(http).GetRecentCapabilityAuditAsync(17, TestContext.Current.CancellationToken);

        route.ShouldBe("/api/audit/capability?limit=17");
        ((int)rows.Count).ShouldBe(1);
        ((string)rows[0].Grant).ShouldBe("tool:files:invoke:write_file");
        ((string)rows[0].IssuedTo).ShouldBe("release-reviewer");
        ((string)rows[0].Outcome).ShouldBe("Deny");
        ((string)rows[0].Reason).ShouldBe("scope-mismatch-marker");
    }

    [Fact]
    public async Task GetRecentCapabilityAuditAsync_DefaultLimit_RequestsRecentTwoHundredRows()
    {
        string? route = null;
        using var http = DashboardPageFixture.Http((request, _) =>
        {
            route = request.RequestUri.ShouldNotBeNull().PathAndQuery;
            return Task.FromResult(DashboardPageFixture.Json("[]"));
        });

        dynamic rows = await DashboardPageFixture.Api(http).GetRecentCapabilityAuditAsync(ct: TestContext.Current.CancellationToken);

        route.ShouldBe("/api/audit/capability?limit=200");
        ((int)rows.Count).ShouldBe(0);
    }

    [Fact]
    public async Task GetCapabilityAuditByTokenAsync_ReservedCharacters_RemainsOneEscapedPathSegment()
    {
        string? route = null;
        using var http = DashboardPageFixture.Http((request, _) =>
        {
            route = request.RequestUri.ShouldNotBeNull().AbsoluteUri;
            return Task.FromResult(DashboardPageFixture.Json("[]"));
        });

        dynamic rows = await DashboardPageFixture.Api(http).GetCapabilityAuditByTokenAsync("token/a?b#c", TestContext.Current.CancellationToken);

        route.ShouldBe("http://dashboard-api.test/api/audit/capability/token%2Fa%3Fb%23c");
        ((int)rows.Count).ShouldBe(0);
    }

    [Fact]
    public async Task SendMessageAsync_ContentWithQuotesAndNewlines_PostsUserMessageAndReadsConversation()
    {
        string? route = null;
        string? body = null;
        using var http = DashboardPageFixture.Http(async (request, cancellation) =>
        {
            route = request.Method + " " + request.RequestUri.ShouldNotBeNull().PathAndQuery;
            body = await request.Content.ShouldNotBeNull().ReadAsStringAsync(cancellation);
            return DashboardPageFixture.Json("""
                {"content":"Release assets verified","conversationId":"review-conversation","usedTools":true,"model":"review-model",
                 "messages":[{"role":"assistant","content":"Release assets verified"}]}
                """);
        });

        dynamic result = await DashboardPageFixture.Api(http).SendMessageAsync("release-lab", "release-reviewer", "Review \"release\"\nassets", TestContext.Current.CancellationToken);

        route.ShouldBe("POST /api/workspaces/release-lab/agents/release-reviewer/messages");
        using var document = JsonDocument.Parse(body.ShouldNotBeNull());
        document.RootElement.GetProperty("content").GetString().ShouldBe("Review \"release\"\nassets");
        document.RootElement.GetProperty("role").GetString().ShouldBe("user");
        ((string)result.Content).ShouldBe("Release assets verified");
        ((string)result.ConversationId).ShouldBe("review-conversation");
        ((bool)result.UsedTools).ShouldBeTrue();
        ((string)result.Messages[0].Role).ShouldBe("assistant");
        ((string)result.Messages[0].Content).ShouldBe("Release assets verified");
    }

    [Theory]
    [InlineData("  quota-marker  ", "quota-marker")]
    [InlineData(" \n ", "(no response body)")]
    public async Task SendMessageAsync_RejectedMessage_PreservesStatusAndUsefulReason(string body, string expected)
    {
        using var http = DashboardPageFixture.Http((_, _) => Task.FromResult(DashboardPageFixture.Json(body, HttpStatusCode.BadRequest)));
        dynamic api = DashboardPageFixture.Api(http);

        var error = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await api.SendMessageAsync("release-lab", "release-reviewer", "Review release", TestContext.Current.CancellationToken);
        });

        error.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        error.Message.ShouldBe("HTTP 400 Bad Request: " + expected);
    }

    [Fact]
    public async Task GetWorkspacesAsync_CancelledRequest_PropagatesCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var http = DashboardPageFixture.Http(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DashboardPageFixture.Json("[]");
        });
        dynamic api = DashboardPageFixture.Api(http);
        Task pending = api.GetWorkspacesAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        pending.IsCanceled.ShouldBeTrue();
    }
}
