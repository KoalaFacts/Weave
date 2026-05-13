namespace Weave.Tools.Connectors;

internal interface IMcpTransport : IAsyncDisposable
{
    Task SendAsync(string json, CancellationToken ct);
    Task<string?> ReceiveAsync(CancellationToken ct);
    bool HasExited { get; }
    int? ExitCode { get; }
    string FormatDiagnosticTail();
}
