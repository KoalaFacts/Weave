using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Agents.Models;

public sealed record ToolResolution
{
    public required string ToolName { get; init; }
    public required string ActorKey { get; init; }
    public required CapabilityToken Token { get; init; }
    public ToolSchema Schema { get; init; } = new();
}
