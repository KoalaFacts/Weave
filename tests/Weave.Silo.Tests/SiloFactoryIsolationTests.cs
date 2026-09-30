using Microsoft.Extensions.DependencyInjection;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests;

public sealed class SiloFactoryIsolationTests
{
    [Fact]
    public async Task CreateClient_ConcurrentFactories_DoNotShareToolState()
    {
        await using var first = new SiloFactory();
        using var firstClient = first.CreateClient();
        await using var second = new SiloFactory();
        using var secondClient = second.CreateClient();

        var root = Path.Combine(Path.GetTempPath(), $"weave-silo-isolation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var id = VirtualActorId.From("isolation/files");
            var firstTool = first.Services.GetRequiredService<IVirtualActorProvider>().GetActor<IToolActor>(id);
            var secondTool = second.Services.GetRequiredService<IVirtualActorProvider>().GetActor<IToolActor>(id);
            var token = first.Services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "isolation",
                IssuedTo = "isolation-test",
                Grants = ["tool:files:connect"],
                Lifetime = TimeSpan.FromMinutes(5)
            });
            await firstTool.ConnectAsync(new ToolSpec
            {
                Name = "files",
                Type = ToolType.FileSystem,
                FileSystem = new FileSystemToolConfig { Root = root }
            }, token);
            (await firstTool.GetHandleAsync()).ShouldNotBeNull().ToolName.ShouldBe("files");
            (await secondTool.GetHandleAsync()).ShouldBeNull();
            await firstTool.DisconnectAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        using var firstHealth = await firstClient.GetAsync("/health", TestContext.Current.CancellationToken);
        using var secondHealth = await secondClient.GetAsync("/health", TestContext.Current.CancellationToken);
        firstHealth.IsSuccessStatusCode.ShouldBeTrue();
        secondHealth.IsSuccessStatusCode.ShouldBeTrue();
    }
}
