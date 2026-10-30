using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Weave.Silo.Tests;

/// <summary>
/// Integration tests for <c>POST /api/workspaces/{id}/agents/{name}/messages/stream</c>
/// — the SSE-streaming counterpart to <c>SendMessageAsync</c>. The silo's composition
/// root wires <see cref="Weave.Agents.Pipeline.FallbackChatClient"/> as the default
/// <c>IChatClient</c>, whose streaming variant yields a text update followed by a
/// usage update — enough to exercise the full chain HTTP → grain →
/// <c>AgentActor.SendStreamingAsync</c> → <c>AgentChatPipeline.ExecuteStreamingAsync</c>
/// → IChatClient → SSE wire format → response stream.
/// </summary>
public sealed class AgentStreamingEndpointTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public AgentStreamingEndpointTests(SiloFactory factory) => _factory = factory;

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
    public async Task SendStream_ReturnsTextEventStreamContentType()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();
        await ActivateAsync(client, ws, agent);

        using var response = await PostStreamAsync(client, ws, agent, "hello");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
    }

    [Fact]
    public async Task SendStream_EmitsTextEventFollowedByCompleteEvent()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();
        await ActivateAsync(client, ws, agent);

        using var response = await PostStreamAsync(client, ws, agent, "<<USER-MARKER>>");

        var events = await ReadEventsAsync(response);
        events.Select(e => e.Event).ShouldBe(["text", "complete"]);
    }

    [Fact]
    public async Task SendStream_TextEventCarriesAssistantText()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();
        await ActivateAsync(client, ws, agent);

        using var response = await PostStreamAsync(client, ws, agent, "<<USER-MARKER>>");

        var events = await ReadEventsAsync(response);
        var text = events.First(e => e.Event == "text");
        using var doc = JsonDocument.Parse(text.Data);
        doc.RootElement.GetProperty("text").GetString()!.ShouldContain("<<USER-MARKER>>");
    }

    [Fact]
    public async Task SendStream_CompleteEventCarriesChatResponse()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();
        await ActivateAsync(client, ws, agent);

        using var response = await PostStreamAsync(client, ws, agent, "<<USER-MARKER>>");

        var events = await ReadEventsAsync(response);
        var complete = events.First(e => e.Event == "complete");
        using var doc = JsonDocument.Parse(complete.Data);
        doc.RootElement.GetProperty("content").GetString()!.ShouldContain("<<USER-MARKER>>");
        doc.RootElement.GetProperty("model").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task SendStream_AgentNotActivated_Returns409()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();

        using var response = await PostStreamAsync(client, ws, agent, "hi");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SendStream_EmptyContent_Returns400()
    {
        using var client = _factory.CreateClient();
        var ws = NewWorkspaceId();
        var agent = NewAgentName();
        await ActivateAsync(client, ws, agent);

        using var response = await PostStreamAsync(client, ws, agent, "");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task ActivateAsync(HttpClient client, string ws, string agent)
    {
        using var activateResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/activate",
            ActivateBody(),
            TestContext.Current.CancellationToken);
        activateResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private static Task<HttpResponseMessage> PostStreamAsync(HttpClient client, string ws, string agent, string content) =>
        client.PostAsJsonAsync(
            $"/api/workspaces/{ws}/agents/{agent}/messages/stream",
            new { Content = content, Role = "user" },
            TestContext.Current.CancellationToken);

    private static async Task<List<(string Event, string Data)>> ReadEventsAsync(HttpResponseMessage response)
    {
        var events = new List<(string, string)>();
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string? eventName = null;
        var data = new StringBuilder();

        foreach (var line in raw.Split('\n'))
        {
            if (line.Length == 0)
            {
                if (eventName is not null)
                    events.Add((eventName, data.ToString()));
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
                eventName = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data.Append(line["data: ".Length..]);
        }

        if (eventName is not null)
            events.Add((eventName, data.ToString()));

        return events;
    }
}
