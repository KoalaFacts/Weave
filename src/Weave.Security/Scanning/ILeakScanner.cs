namespace Weave.Security.Scanning;

public interface ILeakScanner
{
    Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> payload, ScanContext context, CancellationToken ct = default);
    Task<ScanResult> ScanStringAsync(string content, ScanContext context, CancellationToken ct = default);
}
