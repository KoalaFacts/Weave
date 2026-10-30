using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// End-to-end agent lifecycle tests — activate → get → submit task →
/// complete → review, plus validation paths on every POST endpoint.
/// Each test uses a unique workspace to isolate actor state.
/// </summary>
public sealed class AgentLifecycleTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public AgentLifecycleTests(SiloFactory factory) => _factory = factory;

    private static string NewWorkspaceId() => $"ws-{Guid.NewGuid():N}";

    private static object BuildActivateBody(string model = "gpt-4o-mini") => new
    {
        Definition = new
        {
            Model = model,
            MaxConcurrentTasks = 1,
            Tools = Array.Empty<string>(),
            Capabilities = Array.Empty<string>()
        }
    };

    private static async Task ActivateAsync(HttpClient client, string workspaceId, string agentName)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/agents/{agentName}/activate",
            BuildActivateBody(),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
    }

    [Fact]
    public async Task Activate_WithValidDefinition_Returns201()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/agent-a/activate",
            BuildActivateBody(),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("agentName").GetString().ShouldBe("agent-a");
        doc.RootElement.GetProperty("workspaceId").GetString().ShouldBe(ws);
    }

    [Fact]
    public async Task Activate_MissingModel_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/agent-a/activate",
            new { Definition = new { Model = "" } },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAgent_UnknownAgent_Returns404()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/agents/nonexistent",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAgent_AfterActivate_Returns200()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        await ActivateAsync(client, ws, "agent-b");

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/agents/agent-b",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("agentName").GetString().ShouldBe("agent-b");
    }

    [Fact]
    public async Task Deactivate_NeverActivated_IsIdempotentAndReturns204()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsync(
            $"/api/workspaces/{ws}/agents/never-activated/deactivate",
            content: null,
            TestContext.Current.CancellationToken);

        // Deactivating an agent that was never active is a no-op in the actor,
        // which short-circuits on Idle state — the handler correctly surfaces
        // this as 204 rather than erroring, matching the lifecycle contract.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Deactivate_AfterActivate_Returns204()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        await ActivateAsync(client, ws, "agent-c");

        using var response = await client.PostAsync(
            $"/api/workspaces/{ws}/agents/agent-c/deactivate",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SendMessage_EmptyContent_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/x/messages",
            new { Content = "", Role = "user" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendMessage_OverLimitContent_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/x/messages",
            new { Content = new string('a', 50_001), Role = "user" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTasks_UnknownAgent_Returns404()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/agents/nope/tasks",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetTasks_InvalidStatusFilter_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        await ActivateAsync(client, ws, "agent-d");

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/agents/agent-d/tasks?status=notARealStatus",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTasks_ValidStatusFilter_Returns200()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        await ActivateAsync(client, ws, "agent-e");

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/agents/agent-e/tasks?status=AwaitingReview",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetTask_UnknownAgent_Returns404()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/agents/nope/tasks/t_123",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SubmitTask_EmptyDescription_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/agent-x/tasks",
            new { Description = "" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SubmitTask_OverLimitDescription_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/agent-x/tasks",
            new { Description = new string('d', 1001) },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CompleteTask_NoProof_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/a/tasks/t_123/complete",
            new { Success = true, Proof = Array.Empty<object>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CompleteTask_ProofMissingValue_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/a/tasks/t_123/complete",
            new
            {
                Success = true,
                Proof = new[] { new { Type = "Custom", Label = "log", Value = "", Uri = (string?)null } }
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReviewTask_OversizedFeedback_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/a/tasks/t_123/review",
            new { Accepted = true, Feedback = new string('f', 5001) },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListAgents_NewWorkspace_Returns200WithArray()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/agents",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldStartWith("[");
    }

    [Fact]
    public async Task CompleteTask_ValidProof_Returns200WithAwaitingReview()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        await ActivateAsync(client, ws, "agent-c");

        using var submit = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/agent-c/tasks",
            new { Description = "Implement feature" },
            TestContext.Current.CancellationToken);
        var submitBody = await submit.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        submit.StatusCode.ShouldBe(HttpStatusCode.Created, submitBody);
        using var submitDoc = JsonDocument.Parse(submitBody);
        var taskId = submitDoc.RootElement.GetProperty("taskId").GetString();
        taskId.ShouldNotBeNullOrWhiteSpace();

        using var complete = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/agent-c/tasks/{taskId}/complete",
            new
            {
                Success = true,
                Proof = new[]
                {
                    new { Type = "CiStatus", Label = "CI", Value = "passed" }
                }
            },
            TestContext.Current.CancellationToken);
        var completeBody = await complete.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        complete.StatusCode.ShouldBe(HttpStatusCode.OK, completeBody);
        using var completeDoc = JsonDocument.Parse(completeBody);
        completeDoc.RootElement.GetProperty("status").GetString().ShouldBe("AwaitingReview");
    }
}
