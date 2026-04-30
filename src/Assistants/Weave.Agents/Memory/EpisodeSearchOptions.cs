namespace Weave.Agents.Models;

public sealed record EpisodeSearchOptions
{
    public string? AgentName { get; init; }
    public string? Tag { get; init; }
    public DateTimeOffset? Since { get; init; }
    public bool PreferRecent { get; init; }
}
