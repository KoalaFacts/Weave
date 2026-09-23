using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Weave.Workspaces.Manifest;
namespace Weave.Silo.Tests;

/// <summary>
/// Cross-domain integration tests that exercise the full
/// workspace → agent → tool lifecycle through the HTTP API.
/// Proves the seams between workspace startup (which connects tools
/// and activates agents) and individual agent / tool queries work
/// end-to-end with a real Orleans cluster and real actors.
/// </summary>
public sealed class AgentToolIntegrationTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public AgentToolIntegrationTests(SiloFactory factory) => _factory = factory;

    private sealed record StartRequestBody
    {
        public required WorkspaceManifest Manifest { get; init; }
    }

    private static WorkspaceManifest FullManifest() => new()
    {
        Version = "1.0",
        Name = $"test-{Guid.NewGuid():N}",
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["coder"] = new()
            {
                Model = "gpt-4o-mini",
                MaxConcurrentTasks = 2,
                Tools = ["fs-src"]
            }
        },
        Tools = new Dictionary<string, ToolDefinition>
        {
            ["fs-src"] = new()
            {
                Type = "FileSystem",
                FileSystem = new FileSystemToolConfig
                {
                    Root = Path.GetTempPath(),
                    ReadOnly = true
                }
            }
        }
    };

    private static async Task<string> StartWorkspaceAsync(HttpClient client, WorkspaceManifest manifest)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/workspaces",
            new StartRequestBody { Manifest = manifest },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        var wsId = doc.RootElement.GetProperty("workspaceId").GetString();
        wsId.ShouldNotBeNullOrWhiteSpace();
        return wsId!;
    }

    [Fact]
    public async Task WorkspaceStart_ActivatesAgentsAndConnectsTools()
    {
        using var client = _factory.CreateClient();
        var wsId = await StartWorkspaceAsync(client, FullManifest());

        // Verify the agent was auto-activated by the workspace startup.
        using var agentResponse = await client.GetAsync(
            $"/api/workspaces/{wsId}/agents/coder",
            TestContext.Current.CancellationToken);
        agentResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agentBody = await agentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var agentDoc = JsonDocument.Parse(agentBody);
        agentDoc.RootElement.GetProperty("status").GetString().ShouldBe("Active");

        // Verify the tool was connected by the workspace startup.
        using var toolResponse = await client.GetAsync(
            $"/api/workspaces/{wsId}/tools/fs-src",
            TestContext.Current.CancellationToken);
        toolResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var toolBody = await toolResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var toolDoc = JsonDocument.Parse(toolBody);
        toolDoc.RootElement.GetProperty("toolName").GetString().ShouldBe("fs-src");
    }

    [Fact]
    public async Task WorkspaceStart_AgentCanSubmitTask()
    {
        using var client = _factory.CreateClient();
        var wsId = await StartWorkspaceAsync(client, FullManifest());

        // Agent was already activated during workspace start — submit a task directly.
        using var taskResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{wsId}/agents/coder/tasks",
            new { Description = "Refactor module X" },
            TestContext.Current.CancellationToken);

        var taskBody = await taskResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        taskResponse.StatusCode.ShouldBe(HttpStatusCode.Created, taskBody);
        using var taskDoc = JsonDocument.Parse(taskBody);
        taskDoc.RootElement.GetProperty("description").GetString().ShouldBe("Refactor module X");
    }

    [Fact]
    public async Task WorkspaceStop_CleansUpAgentsAndTools()
    {
        using var client = _factory.CreateClient();
        var manifest = FullManifest();
        var wsId = await StartWorkspaceAsync(client, manifest);

        // Stop the workspace.
        using var stopResponse = await client.DeleteAsync(
            $"/api/workspaces/{wsId}",
            TestContext.Current.CancellationToken);
        ((int)stopResponse.StatusCode).ShouldBeLessThan(500);

        // Agent should no longer be active (returns 200 but with non-Active status, or 404).
        using var agentResponse = await client.GetAsync(
            $"/api/workspaces/{wsId}/agents/coder",
            TestContext.Current.CancellationToken);
        if (agentResponse.StatusCode == HttpStatusCode.OK)
        {
            var body = await agentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.GetProperty("status").GetString().ShouldNotBe("Active");
        }

        // Tool should be disconnected.
        using var toolResponse = await client.GetAsync(
            $"/api/workspaces/{wsId}/tools/fs-src",
            TestContext.Current.CancellationToken);
        toolResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListAgents_AfterWorkspaceStart_ReturnsActivatedAgent()
    {
        using var client = _factory.CreateClient();
        var wsId = await StartWorkspaceAsync(client, FullManifest());

        using var response = await client.GetAsync(
            $"/api/workspaces/{wsId}/agents",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var agents = doc.RootElement.EnumerateArray().ToList();
        agents.Count.ShouldBe(1);
        agents[0].GetProperty("agentName").GetString().ShouldBe("coder");
        agents[0].GetProperty("status").GetString().ShouldBe("Active");
    }
}
