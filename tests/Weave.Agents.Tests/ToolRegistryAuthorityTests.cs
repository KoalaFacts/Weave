using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Agents.ToolRegistry;
using Weave.Authority;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Tests;

public sealed class ToolRegistryAuthorityTests
{
    [Fact]
    public async Task ResolveAsync_RestoredAvailabilityWithoutAuthority_ReturnsNoToken()
    {
        using var fx = new Fixture();
        var resolution = await fx.Actor.ResolveAsync("reader", "files");
        resolution.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_ExactReadCapability_ReturnsSignedRestrictedToken()
    {
        using var fx = new Fixture();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], ["tool:files:invoke:read_file", "secret:*"]);
        var resolution = await fx.Actor.ResolveAsync("reader", "files");
        resolution.ShouldNotBeNull();
        fx.Tokens.Validate(resolution.Token).ShouldBeTrue();
        resolution.Token.WorkspaceId.ShouldBe("workspace-a");
        resolution.Token.IssuedTo.ShouldBe("workspace-a/reader");
        resolution.Token.Grants.ShouldBe(["tool:files:invoke:read_file"]);
        resolution.Token.HasGrant(ToolCapability.Invoke("files", "write_file")).ShouldBeFalse();
        resolution.Token.HasGrant(ToolCapability.Connect("files")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("tool:*")]
    [InlineData("tool:files:*")]
    public async Task ResolveAsync_BroadCapability_NarrowsToOneToolInvocation(string capability)
    {
        using var fx = new Fixture();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], [capability]);
        var resolution = await fx.Actor.ResolveAsync("reader", "files");
        resolution.ShouldNotBeNull();
        resolution.Token.Grants.ShouldBe(["tool:files:invoke:*"]);
        resolution.Token.HasGrant(ToolCapability.Invoke("other", "read_file")).ShouldBeFalse();
        resolution.Token.HasGrant(ToolCapability.Connect("files")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("tool:files")]
    [InlineData("tool:other:invoke:*")]
    [InlineData("tool:files:connect")]
    public async Task ResolveAsync_CapabilityDoesNotGrantInvocation_ReturnsNoToken(string capability)
    {
        using var fx = new Fixture();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], [capability]);
        (await fx.Actor.ResolveAsync("reader", "files")).ShouldBeNull();
    }

    [Fact]
    public async Task GrantAgentToolsAsync_CallerMutatesLists_DoesNotExpandAuthority()
    {
        using var fx = new Fixture();
        List<string> tools = ["files"];
        List<string> capabilities = ["tool:files:invoke:read_file"];
        await fx.Actor.GrantAgentToolsAsync("reader", tools, capabilities);
        tools.Add("other");
        capabilities.Add("tool:*");
        var resolution = await fx.Actor.ResolveAsync("reader", "files");
        resolution.ShouldNotBeNull();
        resolution.Token.Grants.ShouldBe(["tool:files:invoke:read_file"]);
        fx.State.IsToolAllowed("reader", "other").ShouldBeFalse();
    }

    [Fact]
    public async Task GrantAgentToolsAsync_EmptyReplacement_RemovesPreviouslyGrantedAuthority()
    {
        using var fx = new Fixture();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], ["tool:files:invoke:read_file"]);
        (await fx.Actor.ResolveAsync("reader", "files")).ShouldNotBeNull();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], []);
        (await fx.Actor.ResolveAsync("reader", "files")).ShouldBeNull();
    }

    [Fact]
    public async Task ConfigureAccessAsync_ReplacesBothMapsAndCapturesInput()
    {
        using var fx = new Fixture();
        var tools = new Dictionary<string, List<string>> { ["reader"] = ["files"] };
        var grants = new Dictionary<string, List<string>> { ["reader"] = ["tool:files:invoke:read_file"] };
        await fx.Actor.ConfigureAccessAsync(tools, grants);
        grants["reader"].Add("tool:*");
        (await fx.Actor.ResolveAsync("reader", "files"))!.Token.Grants.ShouldBe(["tool:files:invoke:read_file"]);
        await fx.Actor.ConfigureAccessAsync(new() { ["reader"] = ["files"] }, new());
        (await fx.Actor.ResolveAsync("reader", "files")).ShouldBeNull();
        fx.State.AgentCapabilities.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_GrantClearedDuringSchemaAwait_DoesNotMintStaleAuthority()
    {
        using var fx = new Fixture();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], ["tool:files:invoke:read_file"]);
        fx.Tool.GetSchemaAsync().Returns(async _ =>
        {
            await fx.Actor.GrantAgentToolsAsync("reader", ["files"], []);
            return new ToolSchema { ToolName = "files" };
        });
        (await fx.Actor.ResolveAsync("reader", "files")).ShouldBeNull();
    }

    [Fact]
    public async Task DisconnectAllAsync_ClearsAvailabilityAndAuthority()
    {
        using var fx = new Fixture();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], ["tool:files:invoke:read_file"]);
        await fx.Actor.DisconnectAllAsync();
        fx.State.AgentCapabilities.ShouldBeEmpty();
        fx.State.AgentToolAccess.ShouldBeEmpty();
        (await fx.Actor.ResolveAsync("reader", "files")).ShouldBeNull();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"weave-grants-{Guid.NewGuid():N}");
        public ToolRegistryState State { get; } = new()
        {
            WorkspaceId = "workspace-a",
            Definitions = new() { ["files"] = new ToolDefinition { Type = "filesystem" } },
            Connections = new() { ["files"] = new ToolConnection { ToolName = "files", ToolType = "filesystem", Status = ToolConnectionStatus.Connected } },
            AgentToolAccess = new() { ["reader"] = ["files"] }
        };
        public IToolActor Tool { get; } = Substitute.For<IToolActor>();
        public CapabilityTokenService Tokens { get; }
        public ToolRegistryActor Actor { get; }

        public Fixture()
        {
            var persistent = Substitute.For<IActorState<ToolRegistryState>>();
            persistent.State.Returns(State);
            var actors = Substitute.For<IVirtualActorProvider>();
            Tool.GetHandleAsync().Returns(new ToolHandle { ToolName = "files", Type = ToolType.FileSystem, IsConnected = true });
            Tool.GetSchemaAsync().Returns(new ToolSchema { ToolName = "files" });
            actors.GetActor<IToolActor>(Arg.Any<VirtualActorId>()).Returns(Tool);
            Tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
            {
                SigningKey = "test-signing-key-that-is-at-least-32-chars-long",
                RevocationDirectory = _directory
            }), TimeProvider.System);
            Actor = new ToolRegistryActor(actors, Tokens, Substitute.For<ILifecycleManager>(),
                Substitute.For<IEventBus>(), TimeProvider.System, NullLogger<ToolRegistryActor>.Instance, persistent);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
