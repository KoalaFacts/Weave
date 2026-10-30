namespace Weave.Security.Scanning;

public sealed record ScanContext
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string SourceComponent { get; init; } = string.Empty;
    public ScanDirection Direction { get; init; }
}
