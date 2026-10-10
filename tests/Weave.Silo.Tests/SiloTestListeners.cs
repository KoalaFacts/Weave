using System.Net;
using Microsoft.AspNetCore.Connections;

namespace Weave.Silo.Tests;

internal sealed class SiloTestListeners : IConnectionListenerFactory, IDisposable, IAsyncDisposable
{
    private readonly SiloTestListener _silo;
    private readonly SiloTestListener _gateway;
    public IPEndPoint SiloEndpoint => (IPEndPoint)_silo.EndPoint;
    public IPEndPoint GatewayEndpoint => (IPEndPoint)_gateway.EndPoint;

    public SiloTestListeners(IConnectionListenerFactory siloFactory, IConnectionListenerFactory gatewayFactory)
    {
        // The pinned native Orleans factory binds synchronously and returns a completed ValueTask.
        _silo = new SiloTestListener(siloFactory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0)).AsTask().GetAwaiter().GetResult());
        SiloTestListener? gateway = null;
        try
        {
            gateway = new SiloTestListener(gatewayFactory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0)).AsTask().GetAwaiter().GetResult());
            _gateway = gateway;
            SiloEndpoint.Port.ShouldBeGreaterThan(0);
            GatewayEndpoint.Port.ShouldBeGreaterThan(0);
            SiloEndpoint.Port.ShouldNotBe(GatewayEndpoint.Port);
            SiloEndpoint.Port.ShouldNotBe(11111);
            SiloEndpoint.Port.ShouldNotBe(30000);
            GatewayEndpoint.Port.ShouldNotBe(11111);
            GatewayEndpoint.Port.ShouldNotBe(30000);
        }
        catch
        {
            try
            {
                gateway?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                _silo.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            throw;
        }
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var listener = endpoint.Equals(SiloEndpoint) ? _silo
            : endpoint.Equals(GatewayEndpoint) ? _gateway
            : throw new InvalidOperationException("Orleans requested an endpoint outside its owned listeners.");
        return ValueTask.FromResult(listener.Take());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _gateway.DisposeAsync();
        }
        finally
        {
            await _silo.DisposeAsync();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
