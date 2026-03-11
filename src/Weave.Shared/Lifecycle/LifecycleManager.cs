using Microsoft.Extensions.Logging;

namespace Weave.Shared.Lifecycle;

public sealed partial class LifecycleManager(ILogger<LifecycleManager> logger) : ILifecycleManager
{
    private readonly ILogger _logger = logger;
    private readonly List<ILifecycleHook> _hooks = [];
    private readonly Lock _lock = new();

    public async Task RunHooksAsync(LifecyclePhase phase, LifecycleContext context, CancellationToken ct)
    {
        ILifecycleHook[] hooks;
        lock (_lock)
        {
            hooks = _hooks
                .Where(h => h.Phase == phase)
                .OrderBy(h => h.Order)
                .ToArray();
        }

        foreach (var hook in hooks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                LogRunningHook(hook.GetType().Name, phase);
                await hook.ExecuteAsync(context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogHookFailed(ex, hook.GetType().Name, phase);
                throw;
            }
        }
    }

    public IDisposable Register(ILifecycleHook hook)
    {
        lock (_lock)
        {
            _hooks.Add(hook);
        }
        return new HookRegistration(this, hook);
    }

    private void Unregister(ILifecycleHook hook)
    {
        lock (_lock)
        {
            _hooks.Remove(hook);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running lifecycle hook {HookType} for phase {Phase}")]
    private partial void LogRunningHook(string hookType, LifecyclePhase phase);

    [LoggerMessage(Level = LogLevel.Error, Message = "Lifecycle hook {HookType} failed for phase {Phase}")]
    private partial void LogHookFailed(Exception ex, string hookType, LifecyclePhase phase);

    private sealed class HookRegistration(LifecycleManager manager, ILifecycleHook hook) : IDisposable
    {
        public void Dispose() => manager.Unregister(hook);
    }
}
