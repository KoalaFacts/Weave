namespace Weave.Shared.Lifecycle;

public interface ILifecycleManager
{
    Task RunHooksAsync(LifecyclePhase phase, LifecycleContext context, CancellationToken ct);
    IDisposable Register(ILifecycleHook hook);
}
