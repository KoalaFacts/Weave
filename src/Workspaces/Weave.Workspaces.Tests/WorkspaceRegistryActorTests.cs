using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Tests;

public sealed class WorkspaceRegistryActorTests
{
    private static IActorState<WorkspaceRegistryState> CreatePersistentState(WorkspaceRegistryState? state = null)
    {
        var persistentState = Substitute.For<IActorState<WorkspaceRegistryState>>();
        persistentState.State.Returns(state ?? new WorkspaceRegistryState());
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return persistentState;
    }

    [Fact]
    public async Task RegisterAsync_AddsWorkspaceIdOnce()
    {
        var persistentState = CreatePersistentState();
        var actor = new WorkspaceRegistryActor(persistentState);

        await actor.RegisterAsync("ws-1");
        await actor.RegisterAsync("ws-1");

        var workspaceIds = await actor.GetWorkspaceIdsAsync();
        workspaceIds.ShouldBe(["ws-1"]);
#pragma warning disable xUnit1051 // NSubstitute verification must match the parameterless overload the actor calls
        await persistentState.Received(1).WriteStateAsync();
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task UnregisterAsync_RemovesWorkspaceId()
    {
        var persistentState = CreatePersistentState(new WorkspaceRegistryState
        {
            WorkspaceIds = ["ws-1", "ws-2"]
        });
        var actor = new WorkspaceRegistryActor(persistentState);

        await actor.UnregisterAsync("ws-1");

        var workspaceIds = await actor.GetWorkspaceIdsAsync();
        workspaceIds.ShouldBe(["ws-2"]);
#pragma warning disable xUnit1051 // NSubstitute verification must match the parameterless overload the actor calls
        await persistentState.Received(1).WriteStateAsync();
#pragma warning restore xUnit1051
    }
}
