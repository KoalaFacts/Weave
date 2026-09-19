using System.Text.Json;
using System.Text.Json.Serialization;
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
        var fx = new Fixture();
        fx.State.AgentToolAccess["reader"] = ["files"];
        (await fx.Actor.ResolveAsync("reader", "files")).ShouldBeNull();
    }

    [Theory]
    [InlineData("tool:files:invoke:read_file", "tool:files:invoke:read_file")]
    [InlineData("tool:*", "tool:files:invoke:*")]
    public async Task ResolveAsync_ExplicitCapabilities_MintsOnlyConstrainedInvocationGrants(string configured, string expected)
    {
        var fx = new Fixture();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], [configured, "secret:*", "tool:other:invoke:*"]);
        var resolution = await fx.Actor.ResolveAsync("reader", "files");
        resolution.ShouldNotBeNull();
        fx.Tokens.Validate(resolution.Token).ShouldBeTrue();
        resolution.Token.Grants.ShouldBe([expected]);
        resolution.Token.HasGrant("tool:files:connect").ShouldBeFalse();
        resolution.Token.HasGrant("secret:key").ShouldBeFalse();
    }

    [Fact]
    public async Task GrantAgentToolsAsync_CallerMutatesListsOrClearsPermissions_DoesNotBroadenAuthority()
    {
        var fx = new Fixture();
        List<string> tools = ["files"];
        List<string> capabilities = ["tool:files:invoke:read_file"];
        await fx.Actor.GrantAgentToolsAsync("reader", tools, capabilities);
        capabilities[0] = "tool:*";
        tools.Add("other");
        var resolution = await fx.Actor.ResolveAsync("reader", "files");
        resolution.ShouldNotBeNull();
        resolution.Token.Grants.ShouldBe(["tool:files:invoke:read_file"]);
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], []);
        (await fx.Actor.ResolveAsync("reader", "files")).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_CapabilitiesClearedDuringSchemaAwait_ReturnsNoToken()
    {
        var fx = new Fixture();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], ["tool:files:invoke:read_file"]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ToolSchema>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Tool.GetSchemaAsync().Returns(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        var pending = fx.Actor.ResolveAsync("reader", "files");
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], []);
        release.SetResult(new ToolSchema { ToolName = "files" });
        (await pending.WaitAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ConfigureAccessAsync_ReplacementWithoutCapabilities_DropsOldAuthority()
    {
        var fx = new Fixture();
        await fx.Actor.GrantAgentToolsAsync("reader", ["files"], ["tool:files:invoke:read_file"]);
        await fx.Actor.ConfigureAccessAsync(new() { ["reader"] = ["files"] }, new());
        (await fx.Actor.ResolveAsync("reader", "files")).ShouldBeNull();
        fx.State.AgentCapabilities["reader"].ShouldBeEmpty();
    }

    [Fact]
    public void Deserialize_OldStateWithoutCapabilities_DoesNotInventGrants()
    {
        var restored = JsonSerializer.Deserialize(
            "{\"WorkspaceId\":\"workspace-a\",\"AgentToolAccess\":{\"reader\":[\"files\"]}}",
            RegistryAuthorityJsonContext.Default.ToolRegistryState);
        restored.ShouldNotBeNull();
        restored.GetInvocationGrants("reader", "files").ShouldBeEmpty();
        restored.GrantTools("reader", ["files"], ["tool:files:invoke:read_file"]);
        var json = JsonSerializer.Serialize(restored, RegistryAuthorityJsonContext.Default.ToolRegistryState);
        var roundTrip = JsonSerializer.Deserialize(json, RegistryAuthorityJsonContext.Default.ToolRegistryState);
        roundTrip.ShouldNotBeNull();
        roundTrip.GetInvocationGrants("reader", "files").ShouldBe(["tool:files:invoke:read_file"]);
    }

    private sealed class Fixture
    {
        public ToolRegistryState State { get; } = new()
        {
            WorkspaceId = "workspace-a",
            Definitions = new() { ["files"] = new ToolDefinition { Type = "filesystem" } },
            Connections = new()
            {
                ["files"] = new ToolConnection
                {
                    ToolName = "files",
                    ToolType = "filesystem",
                    Status = ToolConnectionStatus.Connected
                }
            }
        };
        public IToolActor Tool { get; } = Substitute.For<IToolActor>();
        public CapabilityTokenService Tokens { get; } = new(Options.Create(new CapabilityTokenOptions
        {
            SigningKey = "test-signing-key-that-is-at-least-32-chars-long"
        }), TimeProvider.System);
        public ToolRegistryActor Actor { get; }

        public Fixture()
        {
            var state = Substitute.For<IActorState<ToolRegistryState>>();
            state.State.Returns(State);
            var actors = Substitute.For<IVirtualActorProvider>();
            Tool.GetHandleAsync().Returns(new ToolHandle
            {
                ToolName = "files",
                Type = ToolType.FileSystem,
                IsConnected = true
            });
            Tool.GetSchemaAsync().Returns(new ToolSchema { ToolName = "files" });
            actors.GetActor<IToolActor>(Arg.Any<VirtualActorId>()).Returns(Tool);
            Actor = new ToolRegistryActor(actors, Tokens, Substitute.For<ILifecycleManager>(),
                Substitute.For<IEventBus>(), TimeProvider.System, NullLogger<ToolRegistryActor>.Instance, state);
        }
    }
}

[JsonSerializable(typeof(ToolRegistryState))]
internal partial class RegistryAuthorityJsonContext : JsonSerializerContext
{
}
