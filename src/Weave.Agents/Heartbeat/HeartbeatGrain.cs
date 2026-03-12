using Microsoft.Extensions.Logging;
using Weave.Agents.Grains;
using Weave.Agents.Models;

namespace Weave.Agents.Heartbeat;

public sealed class HeartbeatGrain(
    IGrainFactory grainFactory,
    ILogger<HeartbeatGrain> logger) : Grain, IHeartbeatGrain, IDisposable
{
    private HeartbeatState _state = new();
    private IDisposable? _timer;

    public Task StartAsync(HeartbeatConfig config)
    {
        if (_state.IsRunning || !config.Enabled)
            return Task.CompletedTask;

        var minutes = ParseCronMinutes(config.Cron);

        _state = new HeartbeatState
        {
            IsRunning = true,
            Config = config,
            NextRun = DateTimeOffset.UtcNow.AddMinutes(minutes)
        };

        var interval = TimeSpan.FromMinutes(minutes);
        _timer = this.RegisterGrainTimer(OnHeartbeatTick, interval, interval);

        logger.LogInformation("Heartbeat started for {Key} with interval {Interval}",
            GetAgentKey(), interval);

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _timer?.Dispose();
        _timer = null;
        _state = _state with { IsRunning = false, NextRun = null };

        logger.LogInformation("Heartbeat stopped for {Key}", GetAgentKey());
        return Task.CompletedTask;
    }

    public Task<HeartbeatState> GetStateAsync() => Task.FromResult(_state);

    private async Task OnHeartbeatTick(CancellationToken ct)
    {
        var key = GetAgentKey();
        logger.LogDebug("Heartbeat tick for {Key}", key);

        try
        {
            if (string.Equals(key, "unknown-agent", StringComparison.Ordinal))
                return;

            var agentGrain = grainFactory.GetGrain<IAgentGrain>(key);
            var agentState = await agentGrain.GetStateAsync();

            if (agentState.Status is not Models.AgentStatus.Active)
            {
                logger.LogDebug("Agent {Key} not active, skipping heartbeat tasks", key);
                return;
            }

            // Submit heartbeat tasks to the agent
            foreach (var task in _state.Config.Tasks)
            {
                AgentTaskInfo? taskInfo = null;
                try
                {
                    taskInfo = await agentGrain.SubmitTaskAsync($"[Heartbeat] {task}");
                    var response = await agentGrain.SendAsync(new Models.AgentMessage
                    {
                        Content = task,
                        Metadata = new Dictionary<string, string>
                        {
                            ["source"] = "heartbeat"
                        }
                    });

                    await agentGrain.CompleteTaskAsync(taskInfo.TaskId, success: true);

                    logger.LogDebug("Heartbeat task for {Key} completed with response length {Length}", key, response.Content.Length);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("max concurrent", StringComparison.Ordinal))
                {
                    logger.LogDebug("Agent {Key} at max capacity, deferring heartbeat task", key);
                    break;
                }
                catch (Exception ex)
                {
                    if (taskInfo is not null)
                        await agentGrain.CompleteTaskAsync(taskInfo.TaskId, success: false);

                    logger.LogError(ex, "Heartbeat task failed for {Key}", key);
                }
            }

            _state = _state with
            {
                LastRun = DateTimeOffset.UtcNow,
                ExecutionCount = _state.ExecutionCount + 1,
                NextRun = DateTimeOffset.UtcNow.AddMinutes(ParseCronMinutes(_state.Config.Cron))
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Heartbeat tick failed for {Key}", key);
        }
    }

    /// <summary>
    /// Simple cron parser — extracts the minute interval from patterns like "*/30 * * * *".
    /// Falls back to 30 minutes for complex patterns.
    /// </summary>
    internal static int ParseCronMinutes(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
            return 30;

        var minutePart = parts[0];
        if (minutePart.StartsWith("*/", StringComparison.Ordinal) && int.TryParse(minutePart[2..], out var interval))
            return interval;

        return 30;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private string GetAgentKey()
    {
        try
        {
            return this.GetPrimaryKeyString();
        }
        catch (NullReferenceException)
        {
            return "unknown-agent";
        }
    }
}
