namespace Weave.Shared.Lifecycle;

public interface ILifecycleHook
{
    LifecyclePhase Phase { get; }
    int Order { get; }
    Task ExecuteAsync(LifecycleContext context, CancellationToken ct);
}
