using System.Net;
using Microsoft.AspNetCore.Connections;

namespace Weave.Silo.Tests;

internal sealed class SiloTestListener(IConnectionListener inner) : IConnectionListener
{
    private readonly object _gate = new();
    private bool _taken;
    private Task? _disposal;
    public EndPoint EndPoint => inner.EndPoint;

    public IConnectionListener Take()
    {
        lock (_gate)
        {
            if (_taken || _disposal is not null)
                throw new InvalidOperationException("An owned Orleans listener can only be handed off once.");
            _taken = true;
            return this;
        }
    }

    public ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default) =>
        inner.AcceptAsync(cancellationToken);

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default) =>
        inner.UnbindAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            // Orleans shutdown and DI startup-failure cleanup share the same disposal result.
            _disposal ??= inner.DisposeAsync().AsTask();
            return new ValueTask(_disposal);
        }
    }
}
