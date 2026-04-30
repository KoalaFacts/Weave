namespace Weave.Agents.Models;

public sealed record AgentMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; init; } = [];
    public string? UserId { get; init; }
}