using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weave.Agents.Verification;

namespace Weave.Silo.Tests;

public sealed class AgentManualReviewTests
{
    [Fact]
    public async Task ReviewTask_AutomaticVerificationPaused_AcceptsTask()
    {
        await using var parent = new SiloFactory();
        var pausedVerification = new PausedVerificationDispatcher();
        await using var host = parent.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentVerificationDispatcher>();
                services.AddSingleton<IAgentVerificationDispatcher>(pausedVerification);
            }));
        using var client = host.CreateClient();
        var ws = $"ws-{Guid.NewGuid():N}";
        var agent = $"agent-{Guid.NewGuid():N}";
        using var activateResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/activate",
            new
            {
                Definition = new
                {
                    Model = "gpt-4o-mini",
                    MaxConcurrentTasks = 2,
                    Tools = Array.Empty<string>(),
                    Capabilities = Array.Empty<string>()
                }
            },
            TestContext.Current.CancellationToken);
        activateResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var submitResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks",
            new { Description = "end-to-end task" },
            TestContext.Current.CancellationToken);
        submitResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
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
        using var completeDoc = JsonDocument.Parse(completeBody);
        completeDoc.RootElement.GetProperty("status").GetString().ShouldBe("AwaitingReview");
        pausedVerification.Pending.ShouldContain(request => request.TaskId.ToString() == taskId);

        using var reviewResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/tasks/{taskId}/review",
            new { Accepted = true, Feedback = "looks good" },
            TestContext.Current.CancellationToken);
        var reviewBody = await reviewResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        reviewResponse.StatusCode.ShouldBe(HttpStatusCode.OK, reviewBody);
        using var reviewDoc = JsonDocument.Parse(reviewBody);
        reviewDoc.RootElement.GetProperty("status").GetString().ShouldBe("Accepted");
        reviewDoc.RootElement.GetProperty("proof").GetProperty("reviewFeedback")
            .GetString().ShouldBe("looks good");
    }

    private sealed class PausedVerificationDispatcher : IAgentVerificationDispatcher
    {
        public ConcurrentQueue<AgentVerificationRequest> Pending { get; } = new();

        public ValueTask EnqueueAsync(AgentVerificationRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Pending.Enqueue(request);
            return ValueTask.CompletedTask;
        }
    }
}
