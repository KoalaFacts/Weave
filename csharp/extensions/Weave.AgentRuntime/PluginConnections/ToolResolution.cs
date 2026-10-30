using Weave.Security.Tokens;
using Weave.Tools.Tool;
namespace Weave.Agents.ToolRegistry;

public sealed record ToolResolution
{
    public required string ToolName { get; init; }
    public required string ActorKey { get; init; }
    public required CapabilityToken Token { get; init; }
    public ToolSchema Schema { get; init; } = new();
}
