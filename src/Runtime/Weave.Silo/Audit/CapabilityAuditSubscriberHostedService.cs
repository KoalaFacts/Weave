using Microsoft.Extensions.Hosting;
using Weave.Security.Audit;
using Weave.Security.Events;
using Weave.Shared.Events;

namespace Weave.Silo.Audit;

/// <summary>
/// Subscribes to <see cref="CapabilityAuthorizationEvent"/> on the silo's
/// <see cref="IEventBus"/> and feeds rows into the <see cref="ICapabilityAuditStore"/>.
/// Powers roadmap #3 — capability replay/debugger.
/// </summary>
public sealed class CapabilityAuditSubscriberHostedService(
    IEventBus eventBus,
    ICapabilityAuditStore store) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = eventBus.Subscribe<CapabilityAuthorizationEvent>((evt, _) =>
        {
            store.Record(evt);
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }
}
