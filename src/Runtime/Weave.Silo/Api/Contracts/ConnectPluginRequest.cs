namespace Weave.Silo.Api;

public sealed record ConnectPluginRequest
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string>? Config { get; init; }
}
