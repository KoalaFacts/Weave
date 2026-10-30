namespace Weave.Silo.Invocations;

internal sealed record InvokeToolHttpRequest
{
    public string? InvocationId { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public Dictionary<string, string>? Parameters { get; init; } = [];
    public string? RawInput { get; init; }
}
