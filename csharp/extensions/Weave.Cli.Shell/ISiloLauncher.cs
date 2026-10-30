namespace Weave.Cli.Shell;

internal sealed record SiloAutoStartResult(bool Success, string LogPath, string? Reason);

internal interface ISiloLauncher
{
    /// <summary>
    /// Resolves the on-disk path to the Weave silo (env var override,
    /// configured path, repo-relative source, then deployed
    /// <c>Weave.Silo.dll</c>). Returns null when none can be located.
    /// </summary>
    string? ResolveSiloPath();

    Task<bool> AutoStartServeAsync(CancellationToken ct);

    Task<SiloAutoStartResult> AutoStartServeWithDiagnosticsAsync(CancellationToken ct);
}
