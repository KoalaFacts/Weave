namespace Weave.Agents.Models;

public sealed record EpisodicMemoryState
{
    public Dictionary<string, Episode> Episodes { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;
}
