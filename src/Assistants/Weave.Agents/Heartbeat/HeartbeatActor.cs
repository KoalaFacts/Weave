using Microsoft.Extensions.Logging;
using Weave.Agents.Models;

namespace Weave.Agents.Heartbeat;

public sealed partial class HeartbeatActor(
    IVirtualActorProvider actors,
    IActorTimerRegistry timerRegistry,
    TimeProvider timeProvider,
    ILogger<HeartbeatActor> logger) : IHeartbeatActor, IDisposable
{
    private readonly HeartbeatTickRunner _tickRunner = new(actors, timeProvider, logger);
    private HeartbeatState _state = new();
    private IDisposable? _timer;
    private string? _key;

    public Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _key = key;
        return Task.CompletedTask;
    }

    public Task StartAsync(HeartbeatConfig config)
    {
        if (_state.IsRunning || !config.Enabled)
            return Task.CompletedTask;

        var minutes = HeartbeatSchedule.ParseMinutes(config.Cron);

        _state = new HeartbeatState
        {
            IsRunning = true,
            Config = config,
            NextRun = timeProvider.GetUtcNow().AddMinutes(minutes)
        };

        var interval = TimeSpan.FromMinutes(minutes);
        _timer = timerRegistry.RegisterTimer(OnHeartbeatTick, interval, interval);

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
        _state = await _tickRunner.ExecuteAsync(_state, key, ct);
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private string GetAgentKey() => _key ?? "unknown-agent";

    [LoggerMessage(Level = LogLevel.Information, Message = "Heartbeat started for {Key} with interval {Interval}")]
    private partial void LogHeartbeatStarted(string key, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Heartbeat stopped for {Key}")]
    private partial void LogHeartbeatStopped(string key);
}
