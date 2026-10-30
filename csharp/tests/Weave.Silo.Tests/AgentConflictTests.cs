using System.Net;
using System.Net.Http.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// Tests for 409 Conflict responses from agent endpoints. An agent that
/// hasn't been activated yet rejects mutation operations (send message,
/// submit task) with 409 — the underlying actor throws
/// <see cref="InvalidOperationException"/> which the endpoint maps to
/// <c>409 Conflict</c>.
/// </summary>
public sealed class AgentConflictTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public AgentConflictTests(SiloFactory factory) => _factory = factory;

    private static string NewWorkspaceId() => $"ws-{Guid.NewGuid():N}";

    [Fact]
    public async Task SendMessage_NotActivated_Returns409()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/idle-agent/messages",
            new { Content = "hello", Role = "user" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SubmitTask_NotActivated_Returns409()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/idle-agent/tasks",
            new { Description = "do something" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CompleteTask_NotActivated_Returns409()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/idle-agent/tasks/t_fake/complete",
            new
            {
                Success = true,
                Proof = new[]
                {
                    new { Type = "CiStatus", Label = "build", Value = "green", Uri = (string?)null }
                }
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ReviewTask_NotActivated_Returns409()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/idle-agent/tasks/t_fake/review",
            new { Accepted = true, Feedback = "lgtm" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SendMessage_AfterDeactivate_Returns409()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();

        // Activate, then deactivate — agent returns to Idle.
        using var activateResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/temp-agent/activate",
            new
            {
                Definition = new
                {
                    Model = "gpt-4o-mini",
                    MaxConcurrentTasks = 1,
                    Tools = Array.Empty<string>(),
                    Capabilities = Array.Empty<string>()
                }
            },
            TestContext.Current.CancellationToken);
        activateResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var deactivateResponse = await client.PostAsync(
            $"/api/workspaces/{ws}/agents/temp-agent/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        deactivateResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Now send a message — agent is Idle again.
        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/temp-agent/messages",
            new { Content = "hello after deactivate", Role = "user" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
