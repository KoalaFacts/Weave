using Weave.Agents.Grains;
using Weave.Silo.Api;
using Weave.Shared.Ids;
using Weave.Workspaces.Commands;
using Weave.Workspaces.Grains;
using Weave.Workspaces.Models;
using Weave.Workspaces.Queries;

namespace Weave.Workspaces.Tests;

public sealed class WorkspaceCommandHandlerTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static WorkspaceManifest CreateManifest() => new()
    {
        Name = "test-workspace",
        Version = "1.0",
        Workspace = new WorkspaceConfig
        {
            Network = new NetworkConfig { Name = "weave-test" }
        }
    };

    [Fact]
    public async Task StartWorkspaceHandler_DelegatesToGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var workspaceGrain = Substitute.For<IWorkspaceGrain>();
        var toolRegistry = Substitute.For<IToolRegistryGrain>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();
        var expectedState = new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            Status = WorkspaceStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };

        grainFactory.GetGrain<IWorkspaceGrain>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceGrain);
        grainFactory.GetGrain<IToolRegistryGrain>(TestWorkspaceId.ToString(), null)
            .Returns(toolRegistry);
        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);
        workspaceGrain.StartAsync(Arg.Any<WorkspaceManifest>())
            .Returns(expectedState);
        workspaceGrain.GetStateAsync().Returns(expectedState);

        var handler = new StartWorkspaceHandler(grainFactory);
        var manifest = CreateManifest();
        var command = new StartWorkspaceCommand(TestWorkspaceId, manifest);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(WorkspaceStatus.Running);
        result.WorkspaceId.ShouldBe(TestWorkspaceId);
        await workspaceGrain.Received(1).StartAsync(manifest);
        await toolRegistry.Received(1).ConnectToolsAsync(manifest.Tools);
        await supervisor.Received(1).ActivateAllAsync(manifest);
    }

    [Fact]
    public async Task StopWorkspaceHandler_DelegatesToGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var workspaceGrain = Substitute.For<IWorkspaceGrain>();
        var toolRegistry = Substitute.For<IToolRegistryGrain>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();

        workspaceGrain.GetStateAsync().Returns(new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            ActiveAgents = []
        });

        grainFactory.GetGrain<IWorkspaceGrain>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceGrain);
        grainFactory.GetGrain<IToolRegistryGrain>(TestWorkspaceId.ToString(), null)
            .Returns(toolRegistry);
        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);

        var handler = new StopWorkspaceHandler(grainFactory);
        var command = new StopWorkspaceCommand(TestWorkspaceId);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.ShouldBeTrue();
        await workspaceGrain.Received(1).StopAsync();
        await supervisor.Received(1).DeactivateAllAsync();
        await toolRegistry.Received(1).DisconnectAllAsync();
    }

    [Fact]
    public async Task GetWorkspaceStateHandler_DelegatesToGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var workspaceGrain = Substitute.For<IWorkspaceGrain>();
        var expectedState = new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            Status = WorkspaceStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            ActiveAgents = ["researcher", "coder"]
        };

        grainFactory.GetGrain<IWorkspaceGrain>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceGrain);
        workspaceGrain.GetStateAsync().Returns(expectedState);

        var handler = new GetWorkspaceStateHandler(grainFactory);
        var query = new GetWorkspaceStateQuery(TestWorkspaceId);

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Status.ShouldBe(WorkspaceStatus.Running);
        result.ActiveAgents.Count.ShouldBe(2);
        result.ActiveAgents.ShouldContain("researcher");
    }

    [Fact]
    public async Task GetWorkspaceStateHandler_StoppedWorkspace_ReturnsStoppedState()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var workspaceGrain = Substitute.For<IWorkspaceGrain>();
        var expectedState = new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            Status = WorkspaceStatus.Stopped
        };

        grainFactory.GetGrain<IWorkspaceGrain>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceGrain);
        workspaceGrain.GetStateAsync().Returns(expectedState);

        var handler = new GetWorkspaceStateHandler(grainFactory);
        var query = new GetWorkspaceStateQuery(TestWorkspaceId);

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Status.ShouldBe(WorkspaceStatus.Stopped);
        result.StartedAt.ShouldBeNull();
    }

    [Fact]
    public async Task StartWorkspaceHandler_WhenGrainThrows_Propagates()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var workspaceGrain = Substitute.For<IWorkspaceGrain>();
        var toolRegistry = Substitute.For<IToolRegistryGrain>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();

        grainFactory.GetGrain<IWorkspaceGrain>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceGrain);
        grainFactory.GetGrain<IToolRegistryGrain>(TestWorkspaceId.ToString(), null)
            .Returns(toolRegistry);
        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);
        workspaceGrain.StartAsync(Arg.Any<WorkspaceManifest>())
            .Returns<WorkspaceState>(x => throw new InvalidOperationException("Provisioning failed"));

        var handler = new StartWorkspaceHandler(grainFactory);
        var command = new StartWorkspaceCommand(TestWorkspaceId, CreateManifest());

        await Should.ThrowAsync<InvalidOperationException>(
            () => handler.HandleAsync(command, CancellationToken.None));
    }
}
