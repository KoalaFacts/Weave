namespace Weave.Security.Tokens;

/// <summary>
/// Pairs a freshly-minted <see cref="CapabilityToken"/> with the
/// <see cref="CancellationTokenSource"/> that drives its
/// <see cref="CapabilityToken.CancellationToken"/>. Disposing the source
/// stops the expiry timer and unregisters from the revocation registry.
/// </summary>
public sealed class CapabilityTokenSource : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Action? _onDispose;

    internal CapabilityTokenSource(CapabilityToken token, CancellationTokenSource cts, Action? onDispose)
    {
        Token = token with { CancellationToken = cts.Token };
        _cts = cts;
        _onDispose = onDispose;
    }

    public CapabilityToken Token { get; }

    public void Cancel() => _cts.Cancel();

    public void Dispose()
    {
        _onDispose?.Invoke();
        _cts.Dispose();
    }
}
