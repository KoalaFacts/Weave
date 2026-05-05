namespace Weave.Agents.Users;

public sealed record UserProfileState
{
    public string UserId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public Dictionary<string, string> Preferences { get; init; } = [];
    public List<InteractionRecord> RecentInteractions { get; init; } = [];
    public Dictionary<string, int> TopicFrequency { get; init; } = [];
    public Dictionary<string, string> DomainContext { get; init; } = [];
    public DateTimeOffset? FirstSeenAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public int TotalInteractions { get; set; }
    public string? PreferredModel { get; set; }
    public string? PreferredLanguage { get; set; }
    public int MaxRecentInteractions { get; set; } = 100;
}
