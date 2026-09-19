namespace Weave.Tools.Connectors;

public sealed record DirectHttpToolConfig
{
    public string BaseUrl { get; init; } = string.Empty;
    public string? AuthHeader { get; init; }
}
