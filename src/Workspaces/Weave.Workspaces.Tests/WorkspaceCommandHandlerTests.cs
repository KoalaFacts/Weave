using Weave.Agents.Actors;
using Weave.Shared.Ids;
using Weave.Silo.Api;
using Weave.Workspaces.Commands;
using Weave.Workspaces.Actors;
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
    public async Task StartWorkspaceHandler_DelegatesToActor()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var workspaceActor = Substitute.For<IWorkspaceActor>();
        var workspaceRegistry = Substitute.For<IWorkspaceRegistryActor>();
        var toolRegistry = Substitute.For<IToolRegistryActor>();
        var supervisor = Substitute.For<IAgentSupervisorActor>();
        var expectedState = new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            Status = WorkspaceStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };

        actorFactory.GetGrain<IWorkspaceActor>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceActor);
        actorFactory.GetGrain<IWorkspaceRegistryActor>("active", null)
            .Returns(workspaceRegistry);
        actorFactory.GetGrain<IToolRegistryActor>(TestWorkspaceId.ToString(), null)
            .Returns(toolRegistry);
        actorFactory.GetGrain<IAgentSupervisorActor>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);
        workspaceActor.StartAsync(Arg.Any<WorkspaceManifest>())
            .Returns(expectedState);
        workspaceActor.GetStateAsync().Returns(expectedState);

        var handler = new StartWorkspaceHandler(new TestVirtualActorProvider(actorFactory));
        var manifest = CreateManifest();
        var command = new StartWorkspaceCommand(TestWorkspaceId, manifest);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(WorkspaceStatus.Running);
        result.WorkspaceId.ShouldBe(TestWorkspaceId);
        await workspaceActor.Received(1).StartAsync(manifest);
        await workspaceRegistry.Received(1).RegisterAsync(TestWorkspaceId.ToString());
        await toolRegistry.Received(1).ConnectToolsAsync(manifest.Tools);
        await supervisor.Received(1).ActivateAllAsync(manifest);
    }

    [Fact]
    public async Task StopWorkspaceHandler_DelegatesToActor()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var workspaceActor = Substitute.For<IWorkspaceActor>();
        var workspaceRegistry = Substitute.For<IWorkspaceRegistryActor>();
        var toolRegistry = Substitute.For<IToolRegistryActor>();
        var supervisor = Substitute.For<IAgentSupervisorActor>();

        workspaceActor.GetStateAsync().Returns(new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            ActiveAgents = []
        });

        actorFactory.GetGrain<IWorkspaceActor>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceActor);
        actorFactory.GetGrain<IWorkspaceRegistryActor>("active", null)
            .Returns(workspaceRegistry);
        actorFactory.GetGrain<IToolRegistryActor>(TestWorkspaceId.ToString(), null)
            .Returns(toolRegistry);
        actorFactory.GetGrain<IAgentSupervisorActor>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);

        var handler = new StopWorkspaceHandler(new TestVirtualActorProvider(actorFactory));
        var command = new StopWorkspaceCommand(TestWorkspaceId);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.ShouldBeTrue();
        await workspaceActor.Received(1).StopAsync();
        await supervisor.Received(1).DeactivateAllAsync();
        await toolRegistry.Received(1).DisconnectAllAsync();
        await workspaceRegistry.Received(1).UnregisterAsync(TestWorkspaceId.ToString());
    }

    [Fact]
    public async Task GetAllWorkspaceStatesHandler_ReturnsWorkspaceStatesFromRegistry()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var registry = Substitute.For<IWorkspaceRegistryActor>();
        var workspace1 = Substitute.For<IWorkspaceActor>();
        var workspace2 = Substitute.For<IWorkspaceActor>();
        var workspace1State = new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Running
        };
        var workspace2State = new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-2"),
            Status = WorkspaceStatus.Starting
        };

        actorFactory.GetGrain<IWorkspaceRegistryActor>("active", null)
            .Returns(registry);
        actorFactory.GetGrain<IWorkspaceActor>("ws-1", null)
            .Returns(workspace1);
        actorFactory.GetGrain<IWorkspaceActor>("ws-2", null)
            .Returns(workspace2);
        registry.GetWorkspaceIdsAsync().Returns(["ws-2", "ws-1"]);
        workspace1.GetStateAsync().Returns(workspace1State);
        workspace2.GetStateAsync().Returns(workspace2State);

        var handler = new GetAllWorkspaceStatesHandler(new TestVirtualActorProvider(actorFactory));

        var result = await handler.HandleAsync(new GetAllWorkspaceStatesQuery(), CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].WorkspaceId.ShouldBe(WorkspaceId.From("ws-1"));
        result[1].WorkspaceId.ShouldBe(WorkspaceId.From("ws-2"));
    }

    [Fact]
    public async Task GetWorkspaceStateHandler_DelegatesToActor()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var workspaceActor = Substitute.For<IWorkspaceActor>();
        var expectedState = new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            Status = WorkspaceStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            ActiveAgents = ["researcher", "coder"]
        };

        actorFactory.GetGrain<IWorkspaceActor>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceActor);
        workspaceActor.GetStateAsync().Returns(expectedState);

        var handler = new GetWorkspaceStateHandler(new TestVirtualActorProvider(actorFactory));
        var query = new GetWorkspaceStateQuery(TestWorkspaceId);

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Status.ShouldBe(WorkspaceStatus.Running);
        result.ActiveAgents.Count.ShouldBe(2);
        result.ActiveAgents.ShouldContain("researcher");
    }

    [Fact]
    public async Task GetWorkspaceStateHandler_StoppedWorkspace_ReturnsStoppedState()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var workspaceActor = Substitute.For<IWorkspaceActor>();
        var expectedState = new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            Status = WorkspaceStatus.Stopped
        };

        actorFactory.GetGrain<IWorkspaceActor>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceActor);
        workspaceActor.GetStateAsync().Returns(expectedState);

        var handler = new GetWorkspaceStateHandler(new TestVirtualActorProvider(actorFactory));
        var query = new GetWorkspaceStateQuery(TestWorkspaceId);

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Status.ShouldBe(WorkspaceStatus.Stopped);
        result.StartedAt.ShouldBeNull();
    }

    [Fact]
    public async Task StartWorkspaceHandler_WhenActorThrows_Propagates()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var workspaceActor = Substitute.For<IWorkspaceActor>();
        var workspaceRegistry = Substitute.For<IWorkspaceRegistryActor>();
        var toolRegistry = Substitute.For<IToolRegistryActor>();
        var supervisor = Substitute.For<IAgentSupervisorActor>();

        actorFactory.GetGrain<IWorkspaceActor>(TestWorkspaceId.ToString(), null)
            .Returns(workspaceActor);
        actorFactory.GetGrain<IWorkspaceRegistryActor>("active", null)
            .Returns(workspaceRegistry);
        actorFactory.GetGrain<IToolRegistryActor>(TestWorkspaceId.ToString(), null)
            .Returns(toolRegistry);
        actorFactory.GetGrain<IAgentSupervisorActor>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);
        workspaceActor.StartAsync(Arg.Any<WorkspaceManifest>())
            .Returns<WorkspaceState>(x => throw new InvalidOperationException("Provisioning failed"));

        var handler = new StartWorkspaceHandler(new TestVirtualActorProvider(actorFactory));
        var command = new StartWorkspaceCommand(TestWorkspaceId, CreateManifest());

        await Should.ThrowAsync<InvalidOperationException>(
            () => handler.HandleAsync(command, CancellationToken.None));
        await workspaceRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>());
    }
}
