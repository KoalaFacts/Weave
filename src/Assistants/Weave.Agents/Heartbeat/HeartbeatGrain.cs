using Microsoft.Extensions.Logging;
using Weave.Agents.Grains;
using Weave.Agents.Models;

namespace Weave.Agents.Heartbeat;

public sealed partial class HeartbeatGrain(
    IGrainFactory grainFactory,
    TimeProvider timeProvider,
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
            NextRun = timeProvider.GetUtcNow().AddMinutes(minutes)
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

    internal async Task OnHeartbeatTick(CancellationToken ct)
    {
        var key = GetAgentKey();
        _state = await PerformTickAsync(_state, key, grainFactory, timeProvider, logger, ct);
    }

    /// <summary>
    /// Pure tick logic — takes the current state + collaborators and returns
    /// the next state. Factored out of <see cref="OnHeartbeatTick"/> so tests
    /// can exercise every branch without needing an Orleans grain runtime
    /// (per the "promote private methods to internal for testability" rule
    /// in <c>CLAUDE.md</c>, as demonstrated by <c>ProofValidatorGrain</c>).
    /// </summary>
    internal static async Task<HeartbeatState> PerformTickAsync(
        HeartbeatState state,
        string agentKey,
        IGrainFactory grainFactory,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken ct)
    {
        Log.HeartbeatTick(logger, agentKey);

        try
        {
            if (string.Equals(agentKey, "unknown-agent", StringComparison.Ordinal))
                return state;

            var agentGrain = grainFactory.GetGrain<IAgentGrain>(agentKey);
            var agentState = await agentGrain.GetStateAsync();

            if (agentState.Status is not Models.AgentStatus.Active)
            {
                Log.AgentNotActive(logger, agentKey);
                return state;
            }

            foreach (var task in state.Config.Tasks)
            {
                AgentTaskInfo? taskInfo = null;
                try
                {
                    taskInfo = await agentGrain.SubmitTaskAsync($"[Heartbeat] {task}");
                    var response = await agentGrain.SendAsync(new Models.AgentMessage
                    {
                        Content = task,
                        Metadata = new Dictionary<string, string> { ["source"] = "heartbeat" }
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

                    Log.HeartbeatTaskCompleted(logger, agentKey, response.Content.Length);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("max concurrent", StringComparison.Ordinal))
                {
                    Log.AgentAtMaxCapacity(logger, agentKey);
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

                    Log.HeartbeatTaskFailed(logger, ex, agentKey);
                }
            }

            var tickNow = timeProvider.GetUtcNow();
            return state with
            {
                LastRun = tickNow,
                ExecutionCount = state.ExecutionCount + 1,
                NextRun = tickNow.AddMinutes(ParseCronMinutes(state.Config.Cron))
            };
        }
        catch (Exception ex)
        {
            Log.HeartbeatTickFailed(logger, ex, agentKey);
            return state;
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

    // Static tick-path logging: PerformTickAsync is static (for testability),
    // so it needs static logger helpers rather than instance partial methods.
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat tick for {Key}")]
        public static partial void HeartbeatTick(ILogger logger, string key);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {Key} not active, skipping heartbeat tasks")]
        public static partial void AgentNotActive(ILogger logger, string key);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat task for {Key} completed with response length {Length}")]
        public static partial void HeartbeatTaskCompleted(ILogger logger, string key, int length);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {Key} at max capacity, deferring heartbeat task")]
        public static partial void AgentAtMaxCapacity(ILogger logger, string key);

        [LoggerMessage(Level = LogLevel.Error, Message = "Heartbeat task failed for {Key}")]
        public static partial void HeartbeatTaskFailed(ILogger logger, Exception ex, string key);

        [LoggerMessage(Level = LogLevel.Error, Message = "Heartbeat tick failed for {Key}")]
        public static partial void HeartbeatTickFailed(ILogger logger, Exception ex, string key);
    }
}
