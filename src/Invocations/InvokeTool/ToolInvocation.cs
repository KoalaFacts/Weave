using Weave.Shared.Ids;

namespace Weave.Tools.Tool;

public sealed record ToolInvocation
{
    public string ToolName { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public Dictionary<string, string> Parameters { get; init; } = [];
    public string? RawInput { get; init; }
    public string? ParseWarning { get; init; }

    /// <summary>Supply a stable 32-hex GUID before sending when response-loss recovery matters.</summary>
    public InvocationId? InvocationId { get; init; }
}
