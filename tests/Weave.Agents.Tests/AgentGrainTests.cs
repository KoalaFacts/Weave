using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class AgentGrainTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private const string TestAgentName = "researcher";

    private static IPersistentState<AgentState> CreatePersistentState()
    {
        var state = new AgentState
        {
            AgentId = $"{TestWorkspaceId}/{TestAgentName}",
            WorkspaceId = TestWorkspaceId,
            AgentName = TestAgentName
        };

        var persistentState = Substitute.For<IPersistentState<AgentState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        persistentState.ClearStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static AgentDefinition CreateDefinition(string model = "claude-sonnet-4-20250514", int maxTasks = 2) =>
        new()
        {
            Model = model,
            MaxConcurrentTasks = maxTasks,
            Tools = ["code-search", "shell"]
        };

    private static (AgentGrain Grain, ILifecycleManager Lifecycle, IEventBus EventBus) CreateGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        var lifecycle = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<AgentGrain>>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Substitute.For<IChatClient>());
        var persistentState = CreatePersistentState();

        var grain = new AgentGrain(grainFactory, chatClientFactory, lifecycle, eventBus, logger, persistentState);
        return (grain, lifecycle, eventBus);
    }

    [Fact]
    public async Task ActivateAgentAsync_SetsActiveStatus()
    {
        var (grain, _, _) = CreateGrain();
        var definition = CreateDefinition();

        var state = await grain.ActivateAgentAsync(TestWorkspaceId, definition);

        state.Status.ShouldBe(AgentStatus.Active);
        state.Model.ShouldBe("claude-sonnet-4-20250514");
        state.ActivatedAt.ShouldNotBeNull();
        state.MaxConcurrentTasks.ShouldBe(2);
    }

    [Fact]
    public async Task ActivateAgentAsync_RunsLifecycleHooks()
    {
        var (grain, lifecycle, _) = CreateGrain();

        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await lifecycle.Received(1).RunHooksAsync(
            LifecyclePhase.AgentActivating,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
        await lifecycle.Received(1).RunHooksAsync(
            LifecyclePhase.AgentActivated,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateAgentAsync_PublishesEvent()
    {
        var (grain, _, eventBus) = CreateGrain();

        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await eventBus.Received(1).PublishAsync(
            Arg.Is<Events.AgentActivatedEvent>(e =>
                e.WorkspaceId == TestWorkspaceId &&
                e.Model == "claude-sonnet-4-20250514"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateAgentAsync_WhenAlreadyActive_ReturnsCurrentState()
    {
        var (grain, _, _) = CreateGrain();
        var definition = CreateDefinition();

        var first = await grain.ActivateAgentAsync(TestWorkspaceId, definition);
        var second = await grain.ActivateAgentAsync(TestWorkspaceId, definition);

        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIdleStatus()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await grain.DeactivateAsync();

        var state = await grain.GetStateAsync();
        state.Status.ShouldBe(AgentStatus.Idle);
        state.DeactivatedAt.ShouldNotBeNull();
        state.ConnectedTools.ShouldBeEmpty();
        state.ActiveTasks.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeactivateAsync_PublishesEvent()
    {
        var (grain, _, eventBus) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await grain.DeactivateAsync();

        await eventBus.Received(1).PublishAsync(
            Arg.Is<Events.AgentDeactivatedEvent>(e => e.WorkspaceId == TestWorkspaceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitTaskAsync_ReturnsRunningTask()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        var task = await grain.SubmitTaskAsync("Fix the bug");

        task.TaskId.IsEmpty.ShouldBeFalse();
        task.Description.ShouldBe("Fix the bug");
        task.Status.ShouldBe(AgentTaskStatus.Running);
    }

    [Fact]
    public async Task SubmitTaskAsync_SetsBusyStatus()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await grain.SubmitTaskAsync("Fix the bug");

        var state = await grain.GetStateAsync();
        state.Status.ShouldBe(AgentStatus.Busy);
    }

    [Fact]
    public async Task SubmitTaskAsync_WhenAtMaxConcurrent_Throws()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition(maxTasks: 1));
        await grain.SubmitTaskAsync("Task 1");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => grain.SubmitTaskAsync("Task 2"));
        ex.Message.ShouldContain("max concurrent");
    }

    [Fact]
    public async Task SubmitTaskAsync_WhenNotActive_Throws()
    {
        var (grain, _, _) = CreateGrain();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => grain.SubmitTaskAsync("Task 1"));
        ex.Message.ShouldContain("not active");
    }

    [Fact]
    public async Task CompleteTaskAsync_MarksTaskCompleted()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");

        await grain.CompleteTaskAsync(task.TaskId, success: true);

        var state = await grain.GetStateAsync();
        state.ActiveTasks.ShouldContain(t =>
            t.TaskId == task.TaskId &&
            t.Status == AgentTaskStatus.Completed &&
            t.CompletedAt != null);
    }

    [Fact]
    public async Task CompleteTaskAsync_WhenAllDone_ReturnsToActive()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");

        await grain.CompleteTaskAsync(task.TaskId, success: true);

        var state = await grain.GetStateAsync();
        state.Status.ShouldBe(AgentStatus.Active);
    }

    [Fact]
    public async Task CompleteTaskAsync_WithFailure_MarksAsFailed()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");

        await grain.CompleteTaskAsync(task.TaskId, success: false);

        var state = await grain.GetStateAsync();
        state.ActiveTasks.ShouldContain(t =>
            t.TaskId == task.TaskId &&
            t.Status == AgentTaskStatus.Failed);
    }

    [Fact]
    public async Task CompleteTaskAsync_UnknownTaskId_Throws()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => grain.CompleteTaskAsync(AgentTaskId.From("nonexistent"), success: true));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task ConnectToolAsync_AddsTool()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await grain.ConnectToolAsync("code-search");

        var state = await grain.GetStateAsync();
        state.ConnectedTools.ShouldContain("code-search");
    }

    [Fact]
    public async Task DisconnectToolAsync_RemovesTool()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        await grain.ConnectToolAsync("code-search");

        await grain.DisconnectToolAsync("code-search");

        var state = await grain.GetStateAsync();
        state.ConnectedTools.ShouldNotContain("code-search");
    }
}
