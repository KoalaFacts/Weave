using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Agents.Models;
public sealed record AgentMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; init; } = [];
    public string? UserId { get; init; }
}
public sealed record AgentChatResponse
{
    public string Content { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public List<ConversationMessage> Messages { get; init; } = [];
    public bool UsedTools { get; init; }
    public string? Model { get; init; }
}
public sealed record ToolResolution
{
    public required string ToolName { get; init; }
    public required string ActorKey { get; init; }
    public required CapabilityToken Token { get; init; }
    public ToolSchema Schema { get; init; } = new();
}
