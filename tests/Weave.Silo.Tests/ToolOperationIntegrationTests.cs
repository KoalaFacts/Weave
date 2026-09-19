using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weave.Agents.ToolRegistry;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests;

public sealed class ToolOperationIntegrationTests(SiloFactory factory) : IClassFixture<SiloFactory>
{
    [Fact]
    public async Task WorkspaceStart_ExplicitReadGrant_DeniesWriteUntilAuthorityIsChanged()
    {
        using var client = factory.CreateClient();
        var root = Path.Combine(Path.GetTempPath(), $"weave-host-authority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "note.txt");
        File.WriteAllText(path, "original");
        string? workspaceId = null;
        try
        {
            var manifest = new WorkspaceManifest
            {
                Version = "1.0",
                Name = $"authority-{Guid.NewGuid():N}",
                Agents = new()
                {
                    ["coder"] = new AgentDefinition
                    {
                        Model = "gpt-4o-mini",
                        Tools = ["fs-src"],
                        Capabilities = ["tool:fs-src:invoke:read_file"]
                    }
                },
                Tools = new()
                {
                    ["fs-src"] = new Weave.Workspaces.Manifest.ToolDefinition
                    {
                        Type = "filesystem",
                        FileSystem = new Weave.Workspaces.Manifest.FileSystemToolConfig { Root = root }
                    }
                }
            };
            using var start = await client.PostAsJsonAsync("/api/workspaces",
                new StartRequestBody { Manifest = manifest }, TestContext.Current.CancellationToken);
            var body = await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            start.StatusCode.ShouldBe(HttpStatusCode.Created, body);
            using var document = JsonDocument.Parse(body);
            workspaceId = document.RootElement.GetProperty("workspaceId").GetString();
            workspaceId.ShouldNotBeNullOrWhiteSpace();
            var actors = factory.Services.GetRequiredService<IVirtualActorProvider>();
            var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspaceId));
            var resolution = await registry.ResolveAsync("coder", "fs-src");
            resolution.ShouldNotBeNull();
            resolution.Token.Grants.ShouldBe(["tool:fs-src:invoke:read_file"]);
            var tool = actors.GetActor<IToolActor>(VirtualActorId.From(resolution.ActorKey));
            var read = new ToolInvocation
            {
                ToolName = "fs-src",
                Method = "read_file",
                Parameters = new() { ["path"] = "note.txt" }
            };
            var result = await tool.InvokeAsync(read, resolution.Token);
            result.Success.ShouldBeTrue(result.Error);
            result.Output.ShouldBe("original");
            var write = read with { Method = "write_file", RawInput = "updated" };
            await Should.ThrowAsync<UnauthorizedAccessException>(() => tool.InvokeAsync(write, resolution.Token));
            File.ReadAllText(path).ShouldBe("original");

            // Keep concrete wire collections; compiler-synthesized IReadOnlyList types lack Orleans codecs.
            List<string> tools = ["fs-src"];
            List<string> capabilities = ["tool:fs-src:invoke:write_file"];
            await registry.GrantAgentToolsAsync("coder", tools, capabilities);
            var writer = await registry.ResolveAsync("coder", "fs-src");
            writer.ShouldNotBeNull();
            (await tool.InvokeAsync(write, writer.Token)).Success.ShouldBeTrue();
            File.ReadAllText(path).ShouldBe("updated");
        }
        finally
        {
            if (workspaceId is not null)
            {
                using var cleanup = await client.DeleteAsync($"/api/workspaces/{workspaceId}", TestContext.Current.CancellationToken);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record StartRequestBody
    {
        public required WorkspaceManifest Manifest { get; init; }
    }
}
