namespace Weave.Security.Scanning;

[GenerateSerializer]
public sealed record ScanResult
{
    [Id(0)] public bool HasLeaks { get; init; }
    [Id(1)] public List<LeakFinding> Findings { get; init; } = [];

    public static ScanResult Clean => new() { HasLeaks = false };
}

[GenerateSerializer]
public sealed record LeakFinding
{
    [Id(0)] public string PatternName { get; init; } = string.Empty;
    [Id(1)] public string Description { get; init; } = string.Empty;
    [Id(2)] public int Offset { get; init; }
    [Id(3)] public int Length { get; init; }
}

[GenerateSerializer]
public sealed record ScanContext
{
    [Id(0)] public string WorkspaceId { get; init; } = string.Empty;
    [Id(1)] public string SourceComponent { get; init; } = string.Empty;
    [Id(2)] public ScanDirection Direction { get; init; }
}

public enum ScanDirection
{
    Inbound,
    Outbound
}
