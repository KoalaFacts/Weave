namespace Weave.Cli.Tests;

internal sealed class ServeBoundaryLauncher : ISiloLauncher
{
    public int Resolutions { get; private set; }
    public string? ResolveSiloPath()
    {
        Resolutions++;
        return null;
    }
    public Task<bool> AutoStartServeAsync(CancellationToken ct) =>
        throw new InvalidOperationException("Serve must not delegate another launch.");
    public Task<SiloAutoStartResult> AutoStartServeWithDiagnosticsAsync(CancellationToken ct) =>
        throw new InvalidOperationException("Serve must not delegate another launch.");
}
