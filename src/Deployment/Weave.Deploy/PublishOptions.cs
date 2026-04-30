namespace Weave.Deploy;

public sealed record PublishOptions
{
    public string OutputPath { get; init; } = "./output";
    public string? Registry { get; init; }
    public Dictionary<string, string> Variables { get; init; } = [];
}
