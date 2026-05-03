namespace Weave.Deploy;

public sealed record PublishResult
{
    public bool Success { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public List<string> GeneratedFiles { get; init; } = [];
    public string? Error { get; init; }
}
