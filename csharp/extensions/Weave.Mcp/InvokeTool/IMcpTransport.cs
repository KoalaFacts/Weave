namespace Weave.Tools.Connectors;

internal interface IMcpTransport : IAsyncDisposable
{
    Task SendAsync(string json, CancellationToken ct, McpRequestMetadata? metadata = null);
    Task<string?> ReceiveAsync(CancellationToken ct);
    bool SupportsModernProtocol { get; }
    bool UsesHttpHeaders { get; }
    TimeSpan? ModernProbeTimeout { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    string FormatDiagnosticTail();
}

internal sealed record McpRequestMetadata(string ProtocolVersion, string Method, string? Name = null,
    IReadOnlyDictionary<string, string>? ParameterHeaders = null, bool IncludeRoutingHeaders = true);
