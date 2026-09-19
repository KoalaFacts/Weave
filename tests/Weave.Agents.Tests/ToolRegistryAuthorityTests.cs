using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Agents.ToolRegistry;
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
        var state = Substitute.For<IActorState<ToolRegistryState>>();
        state.State.Returns(new ToolRegistryState
        {
            WorkspaceId = "workspace-a",
            Definitions = new() { ["files"] = new ToolDefinition { Type = "filesystem" } },
            Connections = new() { ["files"] = new ToolConnection { ToolName = "files", Status = ToolConnectionStatus.Connected } },
            AgentToolAccess = new() { ["reader"] = ["files"] }
        });
        var actors = Substitute.For<IVirtualActorProvider>();
        var tool = Substitute.For<IToolActor>();
        tool.GetHandleAsync().Returns(new ToolHandle { ToolName = "files", Type = ToolType.FileSystem, IsConnected = true });
        tool.GetSchemaAsync().Returns(new ToolSchema { ToolName = "files" });
        actors.GetActor<IToolActor>(Arg.Any<VirtualActorId>()).Returns(tool);
        var tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
        {
            SigningKey = "test-signing-key-that-is-at-least-32-chars-long"
        }), TimeProvider.System);
        var actor = new ToolRegistryActor(actors, tokens, Substitute.For<ILifecycleManager>(),
            Substitute.For<IEventBus>(), TimeProvider.System, NullLogger<ToolRegistryActor>.Instance, state);

        var resolution = await actor.ResolveAsync("reader", "files");

        resolution.ShouldBeNull();
    }
}
