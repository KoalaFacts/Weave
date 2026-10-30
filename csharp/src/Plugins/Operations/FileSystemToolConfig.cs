namespace Weave.Tools.Connectors;

public sealed record FileSystemToolConfig
{
    public required string Root { get; init; }
    public bool ReadOnly { get; init; }
    public long MaxReadBytes { get; init; }
    public bool Sandbox { get; init; } = true;
}
