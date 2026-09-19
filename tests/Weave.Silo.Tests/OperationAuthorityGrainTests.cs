using Microsoft.Extensions.DependencyInjection;
using Weave.Silo.VirtualActors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests;

public sealed class OperationAuthorityGrainTests(SiloFactory factory) : IClassFixture<SiloFactory>
{
    [Fact]
    public async Task RegistryGrain_ExplicitReadGrant_AllowsReadAndDeniesWriteThroughRealToolGrain()
    {
        using var client = factory.CreateClient();
        var grains = factory.Services.GetRequiredService<Orleans.IGrainFactory>();
        var workspace = $"op-{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), workspace);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "document.txt");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        var registry = grains.GetGrain<IToolRegistryActorGrain>(workspace);
        try
        {
            await registry.ConnectToolsAsync(new()
            {
                ["files"] = new ToolDefinition { Type = "filesystem", FileSystem = new FileSystemToolConfig { Root = directory } }
            });
            await registry.ConfigureAccessAsync(new() { ["reader"] = ["files"] },
                new() { ["reader"] = ["tool:files:invoke:read_file"] });
            var resolution = await registry.ResolveAsync("reader", "files");
            resolution.ShouldNotBeNull();
            resolution.Token.Grants.ShouldBe(["tool:files:invoke:read_file"]);
            var tool = grains.GetGrain<IToolActorGrain>(resolution.ActorKey);
            var read = await tool.InvokeAsync(new ToolInvocation
            {
                ToolName = "files", Method = "read_file", Parameters = new() { ["path"] = "document.txt" }
            }, resolution.Token);
            read.Success.ShouldBeTrue(read.Error);
            read.Output.ShouldBe("original");
            await Should.ThrowAsync<UnauthorizedAccessException>(() => tool.InvokeAsync(new ToolInvocation
            {
                ToolName = "files", Method = "write_file", RawInput = "not allowed",
                Parameters = new() { ["path"] = "document.txt" }
            }, resolution.Token));
            (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe("original");
            await registry.GrantAgentToolsAsync("reader", ["files"], []);
            (await registry.ResolveAsync("reader", "files")).ShouldBeNull();
        }
        finally
        {
            await registry.DisconnectAllAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
