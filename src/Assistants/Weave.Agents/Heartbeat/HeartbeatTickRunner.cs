using Microsoft.Extensions.Logging;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Chat;
using Weave.Agents.Verification;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;

namespace Weave.Agents.Heartbeat;

internal sealed partial class HeartbeatTickRunner(
    IVirtualActorProvider actors,
    TimeProvider timeProvider,
    ILogger logger)
{
    public async Task<HeartbeatState> ExecuteAsync(
        HeartbeatState state,
        string agentKey,
        CancellationToken ct)
    {
        LogHeartbeatTick(logger, agentKey);

        try
        {
            if (string.Equals(agentKey, "unknown-agent", StringComparison.Ordinal))
                return state;

            var agentActor = actors.GetActor<IAgentActor>(VirtualActorId.From(agentKey));
            var agentState = await agentActor.GetStateAsync();

            if (agentState.Status is not AgentStatus.Active)
            {
                LogAgentNotActive(logger, agentKey);
                return state;
            }

            foreach (var task in state.Config.Tasks)
                if (!await RunTaskAsync(agentActor, agentKey, task))
                    break;

            var tickNow = timeProvider.GetUtcNow();
            return state with
            {
                LastRun = tickNow,
                ExecutionCount = state.ExecutionCount + 1,
                NextRun = tickNow.AddMinutes(HeartbeatSchedule.ParseMinutes(state.Config.Cron))
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or FormatException)
        {
            LogHeartbeatTickFailed(logger, ex, agentKey);
            return state;
        }
    }

    private async Task<bool> RunTaskAsync(IAgentActor agentActor, string agentKey, string task)
    {
        AgentTaskInfo? taskInfo = null;
        try
        {
            taskInfo = await agentActor.SubmitTaskAsync($"[Heartbeat] {task}");
            var response = await agentActor.SendAsync(new AgentMessage
            {
                Content = task,
                Metadata = new Dictionary<string, string> { ["source"] = "heartbeat" }
            });

            await agentActor.CompleteTaskAsync(taskInfo.TaskId, success: true, BuildSuccessProof(response));
            LogHeartbeatTaskCompleted(logger, agentKey, response.Content.Length);
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("max concurrent", StringComparison.Ordinal))
        {
            LogAgentAtMaxCapacity(logger, agentKey);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException or TaskCanceledException)
        {
            if (taskInfo is not null)
                await agentActor.CompleteTaskAsync(taskInfo.TaskId, success: false, BuildFailureProof(ex));

            LogHeartbeatTaskFailed(logger, ex, agentKey);
            return true;
        }
    }

    private static ProofOfWork BuildSuccessProof(AgentChatResponse response) => new()
    {
        Items = [new ProofItem
        {
            Type = ProofType.Custom,
            Label = "Heartbeat response",
            Value = response.Content.Length > 200 ? response.Content[..200] : response.Content
        }]
    };

    private static ProofOfWork BuildFailureProof(Exception ex) => new()
    {
        Items = [new ProofItem
        {
            Type = ProofType.Custom,
            Label = "Heartbeat failure",
            Value = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message
        }]
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat tick for {Key}")]
    private static partial void LogHeartbeatTick(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {Key} not active, skipping heartbeat tasks")]
    private static partial void LogAgentNotActive(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat task for {Key} completed with response length {Length}")]
    private static partial void LogHeartbeatTaskCompleted(ILogger logger, string key, int length);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {Key} at max capacity, deferring heartbeat task")]
    private static partial void LogAgentAtMaxCapacity(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Error, Message = "Heartbeat task failed for {Key}")]
    private static partial void LogHeartbeatTaskFailed(ILogger logger, Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Error, Message = "Heartbeat tick failed for {Key}")]
    private static partial void LogHeartbeatTickFailed(ILogger logger, Exception ex, string key);
}
