namespace Weave.Tools.Models;

public sealed record DaprToolConfig
{
    public string AppId { get; init; } = string.Empty;
    public string MethodName { get; init; } = string.Empty;
}
