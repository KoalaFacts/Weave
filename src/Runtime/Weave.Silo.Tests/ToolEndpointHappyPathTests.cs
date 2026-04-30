using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Weave.Workspaces.Models;

namespace Weave.Silo.Tests;

/// <summary>
/// Happy-path integration tests for <see cref="Api.ToolEndpoints"/>. Starts
/// a workspace with a FileSystem tool in the manifest so the tool connection
/// succeeds in-process without external services.
/// </summary>
public sealed class ToolEndpointHappyPathTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public ToolEndpointHappyPathTests(SiloFactory factory) => _factory = factory;

    private static string UniqueWorkspaceName() => $"test-{Guid.NewGuid():N}";

    private static WorkspaceManifest ManifestWithFileSystemTool(string name) => new()
    {
        Version = "1.0",
        Name = name,
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["helper"] = new()
            {
                Model = "gpt-4o-mini",
                Tools = ["fs-docs"]
            }
        },
        Tools = new Dictionary<string, ToolDefinition>
        {
            ["fs-docs"] = new()
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
            new { Manifest = manifest },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        var wsId = doc.RootElement.GetProperty("workspaceId").GetString();
        wsId.ShouldNotBeNullOrWhiteSpace();
        return wsId!;
    }

    [Fact]
    public async Task ListTools_AfterStartWithTool_ReturnsConnectedTool()
    {
        using var client = _factory.CreateClient();
        var wsId = await StartWorkspaceAsync(client, ManifestWithFileSystemTool(UniqueWorkspaceName()));

        using var response = await client.GetAsync(
            $"/api/workspaces/{wsId}/tools",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var tools = doc.RootElement.EnumerateArray().ToList();
        tools.ShouldNotBeEmpty();
        tools.Any(t => t.GetProperty("toolName").GetString() == "fs-docs").ShouldBeTrue();
    }

    [Fact]
    public async Task GetTool_AfterStartWithTool_ReturnsSingleConnection()
    {
        using var client = _factory.CreateClient();
        var wsId = await StartWorkspaceAsync(client, ManifestWithFileSystemTool(UniqueWorkspaceName()));

        using var response = await client.GetAsync(
            $"/api/workspaces/{wsId}/tools/fs-docs",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("toolName").GetString().ShouldBe("fs-docs");
        doc.RootElement.GetProperty("toolType").GetString().ShouldBe("FileSystem");
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Connected");
    }

    [Fact]
    public async Task GetTool_AfterStop_Returns404()
    {
        using var client = _factory.CreateClient();
        var manifest = ManifestWithFileSystemTool(UniqueWorkspaceName());
        var wsId = await StartWorkspaceAsync(client, manifest);

        using var stopResponse = await client.DeleteAsync(
            $"/api/workspaces/{wsId}",
            TestContext.Current.CancellationToken);
        ((int)stopResponse.StatusCode).ShouldBeLessThan(500);

        using var response = await client.GetAsync(
            $"/api/workspaces/{wsId}/tools/fs-docs",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
