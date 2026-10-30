using System.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Agents.ToolRegistry;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Tests;

public sealed class ToolRegistrySnapshotRecoveryTests
{
    [Fact]
    public void GrantTools_CapabilityEnumerationFails_DoesNotExposeNewToolsWithOldGrants()
    {
        var state = new ToolRegistryState();
        state.GrantTools("agent", ["safe"], ["tool:safe:invoke:read_file", "tool:restricted:invoke:write_file"]);
        var previousTools = state.AgentToolAccess["agent"];
        var previousGrants = state.AgentCapabilities["agent"];

        Should.Throw<InvalidOperationException>(() => state.GrantTools("agent", ["restricted"], new FailingCapabilities()));

        state.AgentToolAccess["agent"].ShouldBeSameAs(previousTools);
        state.AgentCapabilities["agent"].ShouldBeSameAs(previousGrants);
        state.GetInvocationGrants("agent", "restricted").ShouldBeEmpty();
        state.GetInvocationGrants("agent", "safe").ShouldBe(["tool:safe:invoke:read_file"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfigureAccess_InputsAliasOwnedState_CopiesBeforeReplacing(bool aliasTools)
    {
        var state = new ToolRegistryState();
        state.GrantTools("agent", ["files"], ["tool:files:invoke:read_file"]);
        IReadOnlyDictionary<string, List<string>> tools = aliasTools
            ? state.AgentToolAccess : new Dictionary<string, List<string>> { ["agent"] = ["files"] };

        state.ConfigureAccess(tools, state.AgentCapabilities);

        state.AgentToolAccess["agent"].ShouldBe(["files"]);
        state.GetInvocationGrants("agent", "files").ShouldBe(["tool:files:invoke:read_file"]);
    }

    [Fact]
    public void ConfigureAccess_InvalidLaterEntry_PreservesThePreviousCompleteConfiguration()
    {
        var state = new ToolRegistryState();
        state.GrantTools("previous", ["files"], ["tool:files:invoke:read_file"]);
        var previousTools = state.AgentToolAccess["previous"];
        var previousGrants = state.AgentCapabilities["previous"];
        Dictionary<string, List<string>> tools = new() { ["first"] = ["files"], ["invalid"] = null! };

        Should.Throw<ArgumentNullException>(() => state.ConfigureAccess(tools,
            new Dictionary<string, List<string>> { ["first"] = ["tool:files:invoke:write_file"] }));

        state.AgentToolAccess.Keys.ShouldBe(["previous"]);
        state.AgentToolAccess["previous"].ShouldBeSameAs(previousTools);
        state.AgentCapabilities["previous"].ShouldBeSameAs(previousGrants);
        state.GetInvocationGrants("first", "files").ShouldBeEmpty();
    }

    [Fact]
    public void ConfigureAccess_ValidReplacement_OwnsListsAndKeepsMissingAuthorityDenyOnly()
    {
        var state = new ToolRegistryState();
        state.GrantTools("old", ["files"], ["tool:files:invoke:write_file"]);
        Dictionary<string, List<string>> tools = new() { ["agent"] = ["files", "files"], ["ungranted"] = ["files"] };
        Dictionary<string, List<string>> grants = new()
        {
            ["agent"] = ["tool:files:invoke:read_file", "tool:files:invoke:read_file"],
            ["orphan"] = ["tool:files:invoke:write_file"]
        };

        state.ConfigureAccess(tools, grants);
        tools["agent"].Clear();
        grants["agent"][0] = "tool:*";

        state.AgentToolAccess["agent"].ShouldBe(["files"]);
        state.AgentCapabilities["agent"].ShouldBe(["tool:files:invoke:read_file"]);
        state.GetInvocationGrants("ungranted", "files").ShouldBeEmpty();
        state.AgentCapabilities.ContainsKey("orphan").ShouldBeFalse();
        state.AgentCapabilities.ContainsKey("old").ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveAsync_ConnectionBecomesUnavailableDuringSchemaAwait_DoesNotMintToken(bool removeConnection)
    {
        var registry = new ToolRegistryState
        {
            WorkspaceId = "workspace",
            Definitions = new() { ["files"] = new ToolDefinition { Type = "filesystem" } },
            Connections = new()
            {
                ["files"] = new ToolConnection { ToolName = "files", ToolType = "filesystem", Status = ToolConnectionStatus.Connected }
            }
        };
        registry.GrantTools("agent", ["files"], ["tool:files:invoke:read_file"]);
        var state = Substitute.For<IActorState<ToolRegistryState>>();
        state.State.Returns(registry);
        var tool = Substitute.For<IToolActor>();
        tool.GetHandleAsync().Returns(new ToolHandle { ToolName = "files", Type = ToolType.FileSystem, IsConnected = true });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ToolSchema>(TaskCreationOptions.RunContinuationsAsynchronously);
        tool.GetSchemaAsync().Returns(_ => { entered.TrySetResult(); return release.Task; });
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IToolActor>(Arg.Any<VirtualActorId>()).Returns(tool);
        var tokens = Substitute.For<ICapabilityTokenService>();
        tokens.Mint(Arg.Any<CapabilityTokenRequest>()).Returns(new CapabilityToken());
        var actor = new ToolRegistryActor(actors, tokens, Substitute.For<ILifecycleManager>(),
            Substitute.For<IEventBus>(), TimeProvider.System, NullLogger<ToolRegistryActor>.Instance, state);
        var pending = actor.ResolveAsync("agent", "files");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (removeConnection)
                registry.Connections.Remove("files");
            else
                registry.Connections["files"] = new ToolConnection
                {
                    ToolName = "files",
                    ToolType = "filesystem",
                    Status = ToolConnectionStatus.Disconnected
                };
        }
        finally
        {
            release.TrySetResult(new ToolSchema { ToolName = "files" });
        }

        (await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBeNull();
        tokens.DidNotReceive().Mint(Arg.Any<CapabilityTokenRequest>());
    }

    private sealed class FailingCapabilities : IReadOnlyList<string>
    {
        public int Count => 2;
        public string this[int index] => index == 0 ? "tool:safe:invoke:read_file" : throw new InvalidOperationException("snapshot failed");
        public IEnumerator<string> GetEnumerator()
        {
            yield return this[0];
            throw new InvalidOperationException("snapshot failed");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
