using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Xunit;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public class AgentGrainTests
{
    private static AgentDefinition CreateDefinition(string model = "claude-sonnet-4-20250514", int maxTasks = 2) =>
        new()
        {
            Model = model,
            MaxConcurrentTasks = maxTasks,
            Tools = ["code-search", "shell"]
        };

    private static (AgentGrain Grain, ILifecycleManager Lifecycle, IEventBus EventBus) CreateGrain()
    {
        var lifecycle = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<AgentGrain>>();
        var grain = new AgentGrain(lifecycle, eventBus, logger);
        return (grain, lifecycle, eventBus);
    }

    [Fact]
    public async Task ActivateAgentAsync_SetsActiveStatus()
    {
        var (grain, _, _) = CreateGrain();
        var definition = CreateDefinition();

        var state = await grain.ActivateAgentAsync("ws-1", definition);

        state.Status.Should().Be(AgentStatus.Active);
        state.Model.Should().Be("claude-sonnet-4-20250514");
        state.ActivatedAt.Should().NotBeNull();
        state.MaxConcurrentTasks.Should().Be(2);
    }

    [Fact]
    public async Task ActivateAgentAsync_RunsLifecycleHooks()
    {
        var (grain, lifecycle, _) = CreateGrain();

        await grain.ActivateAgentAsync("ws-1", CreateDefinition());

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

        await grain.ActivateAgentAsync("ws-1", CreateDefinition());

        await eventBus.Received(1).PublishAsync(
            Arg.Is<Events.AgentActivatedEvent>(e =>
                e.WorkspaceId == "ws-1" &&
                e.Model == "claude-sonnet-4-20250514"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateAgentAsync_WhenAlreadyActive_ReturnsCurrentState()
    {
        var (grain, _, _) = CreateGrain();
        var definition = CreateDefinition();

        var first = await grain.ActivateAgentAsync("ws-1", definition);
        var second = await grain.ActivateAgentAsync("ws-1", definition);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIdleStatus()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());

        await grain.DeactivateAsync();

        var state = await grain.GetStateAsync();
        state.Status.Should().Be(AgentStatus.Idle);
        state.DeactivatedAt.Should().NotBeNull();
        state.ConnectedTools.Should().BeEmpty();
        state.ActiveTasks.Should().BeEmpty();
    }

    [Fact]
    public async Task DeactivateAsync_PublishesEvent()
    {
        var (grain, _, eventBus) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());

        await grain.DeactivateAsync();

        await eventBus.Received(1).PublishAsync(
            Arg.Is<Events.AgentDeactivatedEvent>(e => e.WorkspaceId == "ws-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitTaskAsync_ReturnsRunningTask()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());

        var task = await grain.SubmitTaskAsync("Fix the bug");

        task.TaskId.Should().NotBeNullOrEmpty();
        task.Description.Should().Be("Fix the bug");
        task.Status.Should().Be(AgentTaskStatus.Running);
    }

    [Fact]
    public async Task SubmitTaskAsync_SetsBusyStatus()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());

        await grain.SubmitTaskAsync("Fix the bug");

        var state = await grain.GetStateAsync();
        state.Status.Should().Be(AgentStatus.Busy);
    }

    [Fact]
    public async Task SubmitTaskAsync_WhenAtMaxConcurrent_Throws()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition(maxTasks: 1));
        await grain.SubmitTaskAsync("Task 1");

        var act = () => grain.SubmitTaskAsync("Task 2");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*max concurrent*");
    }

    [Fact]
    public async Task SubmitTaskAsync_WhenNotActive_Throws()
    {
        var (grain, _, _) = CreateGrain();

        var act = () => grain.SubmitTaskAsync("Task 1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not active*");
    }

    [Fact]
    public async Task CompleteTaskAsync_MarksTaskCompleted()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");

        await grain.CompleteTaskAsync(task.TaskId, success: true);

        var state = await grain.GetStateAsync();
        state.ActiveTasks.Should().ContainSingle(t =>
            t.TaskId == task.TaskId &&
            t.Status == AgentTaskStatus.Completed &&
            t.CompletedAt != null);
    }

    [Fact]
    public async Task CompleteTaskAsync_WhenAllDone_ReturnsToActive()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");

        await grain.CompleteTaskAsync(task.TaskId, success: true);

        var state = await grain.GetStateAsync();
        state.Status.Should().Be(AgentStatus.Active);
    }

    [Fact]
    public async Task CompleteTaskAsync_WithFailure_MarksAsFailed()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");

        await grain.CompleteTaskAsync(task.TaskId, success: false);

        var state = await grain.GetStateAsync();
        state.ActiveTasks.Should().ContainSingle(t =>
            t.TaskId == task.TaskId &&
            t.Status == AgentTaskStatus.Failed);
    }

    [Fact]
    public async Task CompleteTaskAsync_UnknownTaskId_Throws()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());

        var act = () => grain.CompleteTaskAsync("nonexistent", success: true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task ConnectToolAsync_AddsTool()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());

        await grain.ConnectToolAsync("code-search");

        var state = await grain.GetStateAsync();
        state.ConnectedTools.Should().Contain("code-search");
    }

    [Fact]
    public async Task DisconnectToolAsync_RemovesTool()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ActivateAgentAsync("ws-1", CreateDefinition());
        await grain.ConnectToolAsync("code-search");

        await grain.DisconnectToolAsync("code-search");

        var state = await grain.GetStateAsync();
        state.ConnectedTools.Should().NotContain("code-search");
    }
}
