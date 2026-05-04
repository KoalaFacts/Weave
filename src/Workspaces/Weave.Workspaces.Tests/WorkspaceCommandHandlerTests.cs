using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Shared.Ids;
using Weave.Silo.Api;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;
using Weave.Workspaces.Models;

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
        var actors = Substitute.For<IVirtualActorProvider>();
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

        actors.GetActor<IWorkspaceActor>(Arg.Any<VirtualActorId>())
            .Returns(workspaceActor);
        actors.GetActor<IWorkspaceRegistryActor>(Arg.Any<VirtualActorId>())
            .Returns(workspaceRegistry);
        actors.GetActor<IToolRegistryActor>(Arg.Any<VirtualActorId>())
            .Returns(toolRegistry);
        actors.GetActor<IAgentSupervisorActor>(Arg.Any<VirtualActorId>())
            .Returns(supervisor);
        workspaceActor.StartAsync(Arg.Any<WorkspaceManifest>())
            .Returns(expectedState);
        workspaceActor.GetStateAsync().Returns(expectedState);

        var handler = new StartWorkspaceHandler(actors);
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
        var actors = Substitute.For<IVirtualActorProvider>();
        var workspaceActor = Substitute.For<IWorkspaceActor>();
        var workspaceRegistry = Substitute.For<IWorkspaceRegistryActor>();
        var toolRegistry = Substitute.For<IToolRegistryActor>();
        var supervisor = Substitute.For<IAgentSupervisorActor>();

        workspaceActor.GetStateAsync().Returns(new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            ActiveAgents = []
        });

        actors.GetActor<IWorkspaceActor>(Arg.Any<VirtualActorId>())
            .Returns(workspaceActor);
        actors.GetActor<IWorkspaceRegistryActor>(Arg.Any<VirtualActorId>())
            .Returns(workspaceRegistry);
        actors.GetActor<IToolRegistryActor>(Arg.Any<VirtualActorId>())
            .Returns(toolRegistry);
        actors.GetActor<IAgentSupervisorActor>(Arg.Any<VirtualActorId>())
            .Returns(supervisor);

        var handler = new StopWorkspaceHandler(actors);
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
        var actors = Substitute.For<IVirtualActorProvider>();
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

        actors.GetActor<IWorkspaceRegistryActor>(Arg.Any<VirtualActorId>())
            .Returns(registry);
        actors.GetActor<IWorkspaceActor>(VirtualActorId.From("ws-1"))
            .Returns(workspace1);
        actors.GetActor<IWorkspaceActor>(VirtualActorId.From("ws-2"))
            .Returns(workspace2);
        registry.GetWorkspaceIdsAsync().Returns(["ws-2", "ws-1"]);
        workspace1.GetStateAsync().Returns(workspace1State);
        workspace2.GetStateAsync().Returns(workspace2State);

        var handler = new GetAllWorkspaceStatesHandler(actors);

        var result = await handler.HandleAsync(new GetAllWorkspaceStatesQuery(), CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].WorkspaceId.ShouldBe(WorkspaceId.From("ws-1"));
        result[1].WorkspaceId.ShouldBe(WorkspaceId.From("ws-2"));
    }

    [Fact]
    public async Task GetWorkspaceStateHandler_DelegatesToActor()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var workspaceActor = Substitute.For<IWorkspaceActor>();
        var expectedState = new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            Status = WorkspaceStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            ActiveAgents = ["researcher", "coder"]
        };

        actors.GetActor<IWorkspaceActor>(Arg.Any<VirtualActorId>())
            .Returns(workspaceActor);
        workspaceActor.GetStateAsync().Returns(expectedState);

        var handler = new GetWorkspaceStateHandler(actors);
        var query = new GetWorkspaceStateQuery(TestWorkspaceId);

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Status.ShouldBe(WorkspaceStatus.Running);
        result.ActiveAgents.Count.ShouldBe(2);
        result.ActiveAgents.ShouldContain("researcher");
    }

    [Fact]
    public async Task GetWorkspaceStateHandler_StoppedWorkspace_ReturnsStoppedState()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var workspaceActor = Substitute.For<IWorkspaceActor>();
        var expectedState = new WorkspaceState
        {
            WorkspaceId = TestWorkspaceId,
            Status = WorkspaceStatus.Stopped
        };

        actors.GetActor<IWorkspaceActor>(Arg.Any<VirtualActorId>())
            .Returns(workspaceActor);
        workspaceActor.GetStateAsync().Returns(expectedState);

        var handler = new GetWorkspaceStateHandler(actors);
        var query = new GetWorkspaceStateQuery(TestWorkspaceId);

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Status.ShouldBe(WorkspaceStatus.Stopped);
        result.StartedAt.ShouldBeNull();
    }

    [Fact]
    public async Task StartWorkspaceHandler_WhenActorThrows_Propagates()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var workspaceActor = Substitute.For<IWorkspaceActor>();
        var workspaceRegistry = Substitute.For<IWorkspaceRegistryActor>();
        var toolRegistry = Substitute.For<IToolRegistryActor>();
        var supervisor = Substitute.For<IAgentSupervisorActor>();

        actors.GetActor<IWorkspaceActor>(Arg.Any<VirtualActorId>())
            .Returns(workspaceActor);
        actors.GetActor<IWorkspaceRegistryActor>(Arg.Any<VirtualActorId>())
            .Returns(workspaceRegistry);
        actors.GetActor<IToolRegistryActor>(Arg.Any<VirtualActorId>())
            .Returns(toolRegistry);
        actors.GetActor<IAgentSupervisorActor>(Arg.Any<VirtualActorId>())
            .Returns(supervisor);
        workspaceActor.StartAsync(Arg.Any<WorkspaceManifest>())
            .Returns<WorkspaceState>(x => throw new InvalidOperationException("Provisioning failed"));

        var handler = new StartWorkspaceHandler(actors);
        var command = new StartWorkspaceCommand(TestWorkspaceId, CreateManifest());

        await Should.ThrowAsync<InvalidOperationException>(
            () => handler.HandleAsync(command, CancellationToken.None));
        await workspaceRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>());
    }
}
