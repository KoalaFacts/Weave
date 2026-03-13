using Weave.Workspaces.Grains;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Tests;

public sealed class WorkspaceRegistryGrainTests
{
    private static IPersistentState<WorkspaceRegistryState> CreatePersistentState(WorkspaceRegistryState? state = null)
    {
        var persistentState = Substitute.For<IPersistentState<WorkspaceRegistryState>>();
        persistentState.State.Returns(state ?? new WorkspaceRegistryState());
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    [Fact]
    public async Task RegisterAsync_AddsWorkspaceIdOnce()
    {
        var persistentState = CreatePersistentState();
        var grain = new WorkspaceRegistryGrain(persistentState);

        await grain.RegisterAsync("ws-1");
        await grain.RegisterAsync("ws-1");

        var workspaceIds = await grain.GetWorkspaceIdsAsync();
        workspaceIds.ShouldBe(["ws-1"]);
        await persistentState.Received(1).WriteStateAsync();
    }

    [Fact]
    public async Task UnregisterAsync_RemovesWorkspaceId()
    {
        var persistentState = CreatePersistentState(new WorkspaceRegistryState
        {
            WorkspaceIds = ["ws-1", "ws-2"]
        });
        var grain = new WorkspaceRegistryGrain(persistentState);

        await grain.UnregisterAsync("ws-1");

        var workspaceIds = await grain.GetWorkspaceIdsAsync();
        workspaceIds.ShouldBe(["ws-2"]);
        await persistentState.Received(1).WriteStateAsync();
    }
}
