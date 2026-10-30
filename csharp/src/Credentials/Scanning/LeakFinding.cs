namespace Weave.Security.Scanning;

public sealed record LeakFinding
{
    public string PatternName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Offset { get; init; }
    public int Length { get; init; }
}
