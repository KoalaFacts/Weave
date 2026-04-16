namespace Weave.Agents.Models;

[GenerateSerializer]
public sealed record UserProfileState
{
    [Id(0)] public string UserId { get; set; } = string.Empty;
    [Id(1)] public string WorkspaceId { get; set; } = string.Empty;
    [Id(2)] public Dictionary<string, string> Preferences { get; init; } = [];
    [Id(3)] public List<InteractionRecord> RecentInteractions { get; init; } = [];
    [Id(4)] public Dictionary<string, int> TopicFrequency { get; init; } = [];
    [Id(5)] public Dictionary<string, string> DomainContext { get; init; } = [];
    [Id(6)] public DateTimeOffset? FirstSeenAt { get; set; }
    [Id(7)] public DateTimeOffset? LastSeenAt { get; set; }
    [Id(8)] public int TotalInteractions { get; set; }
    [Id(9)] public string? PreferredModel { get; set; }
    [Id(10)] public string? PreferredLanguage { get; set; }
    [Id(11)] public int MaxRecentInteractions { get; set; } = 100;
}

[GenerateSerializer]
public sealed record InteractionRecord
{
    [Id(0)] public required string AgentName { get; init; }
    [Id(1)] public required string Summary { get; init; }
    [Id(2)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    [Id(3)] public List<string> Topics { get; init; } = [];
    [Id(4)] public bool WasSatisfied { get; init; } = true;
}
