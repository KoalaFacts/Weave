using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// Happy-path integration tests for <see cref="Api.AgentEndpoints"/>. The
/// Silo's composition root wires <c>FallbackChatClient</c> as the default
/// <c>IChatClient</c>, so these tests can drive the full chain —
/// HTTP → CQRS → actor → chat pipeline → in-process LLM stub — without
/// any external network calls or mocks.
/// </summary>
public sealed class AgentEndpointHappyPathTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public AgentEndpointHappyPathTests(SiloFactory factory) => _factory = factory;

    private static string NewWorkspaceId() => $"ws-{Guid.NewGuid():N}";
    private static string NewAgentName() => $"agent-{Guid.NewGuid():N}";

    private static object ActivateBody() => new
    {
        Definition = new
        {
            Model = "gpt-4o-mini",
            MaxConcurrentTasks = 2,
            Tools = Array.Empty<string>(),
            Capabilities = Array.Empty<string>()
        }
    };

    [Fact]
    public async Task Activate_ThenSendMessage_ReturnsChatResponse()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();

        using var activateResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/activate",
            ActivateBody(),
            TestContext.Current.CancellationToken);
        activateResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var messageResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/messages",
            new { Content = "hello", Role = "user" },
            TestContext.Current.CancellationToken);

        var body = await messageResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        messageResponse.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("content").GetString();
        content.ShouldNotBeNull();
        content.ShouldContain("hello");
    }

    [Fact]
    public async Task Activate_ThenSubmitTask_ReturnsTask201()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();

        using var activateResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/activate",
            ActivateBody(),
            TestContext.Current.CancellationToken);
        activateResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var taskResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks",
            new { Description = "review open PRs" },
            TestContext.Current.CancellationToken);

        var body = await taskResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        taskResponse.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("description").GetString().ShouldBe("review open PRs");
    }

    [Fact]
    public async Task Activate_ThenListTasks_ReturnsArrayIncludingSubmitted()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();
        await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/activate", ActivateBody(),
            TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks",
            new { Description = "t1" },
            TestContext.Current.CancellationToken);

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("t1");
    }

    [Fact]
    public async Task Activate_ThenSubmit_ThenGetSingleTask_Returns200()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();
        await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/activate", ActivateBody(),
            TestContext.Current.CancellationToken);
        using var submitResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks",
            new { Description = "t1" },
            TestContext.Current.CancellationToken);
        using var submitDoc = JsonDocument.Parse(await submitResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var taskId = submitDoc.RootElement.GetProperty("taskId").GetString();

        using var response = await client.GetAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks/{taskId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Activate_SubmitTask_Complete_Review_FullLifecycle()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();
        await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/activate", ActivateBody(),
            TestContext.Current.CancellationToken);
        using var submitResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks",
            new { Description = "end-to-end task" },
            TestContext.Current.CancellationToken);
        using var submitDoc = JsonDocument.Parse(await submitResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var taskId = submitDoc.RootElement.GetProperty("taskId").GetString();

        using var completeResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks/{taskId}/complete",
            new
            {
                Success = true,
                Proof = new[]
                {
                    new { Type = "CiStatus", Label = "build", Value = "green", Uri = (string?)null },
                    new { Type = "TestResults", Label = "unit", Value = "passed", Uri = (string?)null }
                }
            },
            TestContext.Current.CancellationToken);
        var completeBody = await completeResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        completeResponse.StatusCode.ShouldBe(HttpStatusCode.OK, completeBody);

        using var reviewResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks/{taskId}/review",
            new { Accepted = true, Feedback = "looks good" },
            TestContext.Current.CancellationToken);
        var reviewBody = await reviewResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        reviewResponse.StatusCode.ShouldBe(HttpStatusCode.OK, reviewBody);
    }
}
