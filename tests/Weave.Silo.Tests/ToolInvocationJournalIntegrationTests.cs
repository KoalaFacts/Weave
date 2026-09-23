using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weave.Agents.ToolRegistry;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests;

public sealed class ToolInvocationJournalIntegrationTests(SiloFactory factory) : IClassFixture<SiloFactory>
{
    [Fact]
    public async Task InvokeAsync_CallerProvidedId_ReturnsThatInvocationId()
    {
        await using var scenario = await CreateScenarioAsync();
        var id = Guid.NewGuid().ToString("N");
        var result = await scenario.Tool.InvokeAsync(Request(id, "updated"), scenario.Token);
        result.Success.ShouldBeTrue(result.Error);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        json.RootElement.TryGetProperty("InvocationId", out var actual).ShouldBeTrue(
            "Every dispatched invocation must expose its durable correlation ID.");
        actual.GetString().ShouldBe(id);
    }

    [Fact]
    public async Task InvokeAsync_SameIdAndInput_DoesNotDispatchTheWriteAgain()
    {
        await using var scenario = await CreateScenarioAsync();
        var request = Request(Guid.NewGuid().ToString("N"), "updated");
        (await scenario.Tool.InvokeAsync(request, scenario.Token)).Success.ShouldBeTrue();
        File.ReadAllText(scenario.Path).ShouldBe("updated");
        File.WriteAllText(scenario.Path, "changed outside this invocation");

        await scenario.Tool.InvokeAsync(request, scenario.Token);

        File.ReadAllText(scenario.Path).ShouldBe("changed outside this invocation",
            "Resubmitting a logical invocation must not repeat its external effect.");
    }

    [Fact]
    public async Task InvokeAsync_SameIdWithChangedInput_RejectsWithoutAnotherWrite()
    {
        await using var scenario = await CreateScenarioAsync();
        var id = Guid.NewGuid().ToString("N");
        (await scenario.Tool.InvokeAsync(Request(id, "first"), scenario.Token)).Success.ShouldBeTrue();

        var conflict = await scenario.Tool.InvokeAsync(Request(id, "different"), scenario.Token);

        conflict.Success.ShouldBeFalse();
        File.ReadAllText(scenario.Path).ShouldBe("first");
    }

    // Exercise the additive wire field before production implements it. The baseline
    // deserializes the existing fields but ignores InvocationId, exposing the defect.
    private static ToolInvocation Request(string id, string content) =>
        JsonSerializer.Deserialize<ToolInvocation>(JsonSerializer.Serialize(new
        {
            InvocationId = id,
            ToolName = "fs-journal",
            Method = "write_file",
            Parameters = new Dictionary<string, string> { ["path"] = "note.txt" },
            RawInput = content
        }))!;

    private async Task<Scenario> CreateScenarioAsync()
    {
        var client = factory.CreateClient();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "note.txt");
        File.WriteAllText(path, "original");
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = $"journal-{Guid.NewGuid():N}",
            Agents = new()
            {
                ["writer"] = new AgentDefinition
                {
                    Model = "gpt-4o-mini",
                    Tools = ["fs-journal"],
                    Capabilities = ["tool:fs-journal:invoke:write_file"]
                }
            },
            Tools = new()
            {
                ["fs-journal"] = new Weave.Workspaces.Manifest.ToolDefinition
                {
                    Type = "filesystem",
                    FileSystem = new Weave.Workspaces.Manifest.FileSystemToolConfig { Root = root }
                }
            }
        };
        using var start = await client.PostAsJsonAsync("/api/workspaces", new { Manifest = manifest },
            TestContext.Current.CancellationToken);
        var body = await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        var workspace = document.RootElement.GetProperty("workspaceId").GetString()!;
        var actors = factory.Services.GetRequiredService<IVirtualActorProvider>();
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspace));
        var resolution = (await registry.ResolveAsync("writer", "fs-journal")).ShouldNotBeNull();
        return new Scenario(client, root, path, workspace,
            actors.GetActor<IToolActor>(VirtualActorId.From(resolution.ActorKey)), resolution.Token);
    }

    private sealed class Scenario(
        HttpClient client, string root, string path, string workspace,
        IToolActor tool, CapabilityToken token) : IAsyncDisposable
    {
        public string Path { get; } = path;
        public IToolActor Tool { get; } = tool;
        public CapabilityToken Token { get; } = token;

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var response = await client.DeleteAsync($"/api/workspaces/{workspace}",
                    TestContext.Current.CancellationToken);
            }
            finally
            {
                client.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
