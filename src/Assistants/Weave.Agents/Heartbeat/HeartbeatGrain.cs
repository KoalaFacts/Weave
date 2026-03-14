using Microsoft.Extensions.Logging;
using Weave.Agents.Grains;
using Weave.Agents.Models;

namespace Weave.Agents.Heartbeat;

public sealed partial class HeartbeatGrain(
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

        LogHeartbeatStarted(GetAgentKey(), interval);

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _timer?.Dispose();
        _timer = null;
        _state = _state with { IsRunning = false, NextRun = null };

        LogHeartbeatStopped(GetAgentKey());
        return Task.CompletedTask;
    }

    public Task<HeartbeatState> GetStateAsync() => Task.FromResult(_state);

    private async Task OnHeartbeatTick(CancellationToken ct)
    {
        var key = GetAgentKey();
        LogHeartbeatTick(key);

        try
        {
            if (string.Equals(key, "unknown-agent", StringComparison.Ordinal))
                return;

            var agentGrain = grainFactory.GetGrain<IAgentGrain>(key);
            var agentState = await agentGrain.GetStateAsync();

            if (agentState.Status is not Models.AgentStatus.Active)
            {
                LogAgentNotActive(key);
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

                    var proof = new Models.ProofOfWork
                    {
                        Items = [new Models.ProofItem
                        {
                            Type = Models.ProofType.Custom,
                            Label = "Heartbeat response",
                            Value = response.Content.Length > 200
                                ? response.Content[..200]
                                : response.Content
                        }]
                    };
                    await agentGrain.CompleteTaskAsync(taskInfo.TaskId, success: true, proof);

                    LogHeartbeatTaskCompleted(key, response.Content.Length);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("max concurrent", StringComparison.Ordinal))
                {
                    LogAgentAtMaxCapacity(key);
                    break;
                }
                catch (Exception ex)
                {
                    if (taskInfo is not null)
                    {
                        var failProof = new Models.ProofOfWork
                        {
                            Items = [new Models.ProofItem
                            {
                                Type = Models.ProofType.Custom,
                                Label = "Heartbeat failure",
                                Value = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message
                            }]
                        };
                        await agentGrain.CompleteTaskAsync(taskInfo.TaskId, success: false, failProof);
                    }

                    LogHeartbeatTaskFailed(ex, key);
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
            LogHeartbeatTickFailed(ex, key);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Heartbeat started for {Key} with interval {Interval}")]
    private partial void LogHeartbeatStarted(string key, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Heartbeat stopped for {Key}")]
    private partial void LogHeartbeatStopped(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat tick for {Key}")]
    private partial void LogHeartbeatTick(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {Key} not active, skipping heartbeat tasks")]
    private partial void LogAgentNotActive(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat task for {Key} completed with response length {Length}")]
    private partial void LogHeartbeatTaskCompleted(string key, int length);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {Key} at max capacity, deferring heartbeat task")]
    private partial void LogAgentAtMaxCapacity(string key);

    [LoggerMessage(Level = LogLevel.Error, Message = "Heartbeat task failed for {Key}")]
    private partial void LogHeartbeatTaskFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Error, Message = "Heartbeat tick failed for {Key}")]
    private partial void LogHeartbeatTickFailed(Exception ex, string key);
}
