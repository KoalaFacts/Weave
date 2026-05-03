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
/// <remarks>
/// Startup ordering note: any authorize call that runs before this hosted
/// service's <see cref="StartAsync"/> lands its subscription will not be
/// recorded. In practice the silo activates plugin/agent grains lazily on
/// first request, so this gap closes before any external traffic — but a
/// future hosted service that authorizes during its own startup may drop
/// rows. If that becomes a concern, register this service first or move the
/// subscription into the service registrar.
/// </remarks>
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
