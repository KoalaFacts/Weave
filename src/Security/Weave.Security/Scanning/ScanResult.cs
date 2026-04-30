namespace Weave.Security.Scanning;

public sealed record ScanResult
{
    public bool HasLeaks { get; init; }
    public List<LeakFinding> Findings { get; init; } = [];

    public static ScanResult Clean => new() { HasLeaks = false };
}
