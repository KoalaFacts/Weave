namespace Weave.Security.Scanning;
public sealed record ScanResult
{
    public bool HasLeaks { get; init; }
    public List<LeakFinding> Findings { get; init; } = [];

    public static ScanResult Clean => new() { HasLeaks = false };
}
public sealed record LeakFinding
{
    public string PatternName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Offset { get; init; }
    public int Length { get; init; }
}
public sealed record ScanContext
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string SourceComponent { get; init; } = string.Empty;
    public ScanDirection Direction { get; init; }
}

public enum ScanDirection
{
    Inbound,
    Outbound
}
