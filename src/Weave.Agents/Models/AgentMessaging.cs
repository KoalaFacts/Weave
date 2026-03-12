using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Agents.Models;

[GenerateSerializer]
public sealed record AgentMessage
{
    [Id(0)] public string Role { get; init; } = "user";
    [Id(1)] public string Content { get; init; } = string.Empty;
    [Id(2)] public Dictionary<string, string> Metadata { get; init; } = [];
}

[GenerateSerializer]
public sealed record AgentChatResponse
{
    [Id(0)] public string Content { get; init; } = string.Empty;
    [Id(1)] public string ConversationId { get; init; } = string.Empty;
    [Id(2)] public List<ConversationMessage> Messages { get; init; } = [];
    [Id(3)] public bool UsedTools { get; init; }
    [Id(4)] public string? Model { get; init; }
}

[GenerateSerializer]
public sealed record ToolResolution
{
    [Id(0)] public required string ToolName { get; init; }
    [Id(1)] public required string GrainKey { get; init; }
    [Id(2)] public required CapabilityToken Token { get; init; }
    [Id(3)] public ToolSchema Schema { get; init; } = new();
}
