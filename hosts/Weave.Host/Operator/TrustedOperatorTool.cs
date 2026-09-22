using Weave.Tools.Tool;

namespace Weave.Silo.Operator;

// Deployment configuration, never an HTTP request body.
public sealed record TrustedOperatorTool
{
    public string WorkspaceId { get; init; } = string.Empty;
    public ToolSpec Tool { get; init; } = new();
}
