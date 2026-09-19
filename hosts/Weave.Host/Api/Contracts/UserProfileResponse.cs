using Weave.Agents.Users;
namespace Weave.Silo.Api;

public sealed record UserProfileResponse
{
    public required string UserId { get; init; }
    public required string WorkspaceId { get; init; }
    public Dictionary<string, string> Preferences { get; init; } = [];
    public Dictionary<string, int> TopicFrequency { get; init; } = [];
    public Dictionary<string, string> DomainContext { get; init; } = [];
    public DateTimeOffset? FirstSeenAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public int TotalInteractions { get; init; }

    public static UserProfileResponse FromState(UserProfileState state) => new()
    {
        UserId = state.UserId,
        WorkspaceId = state.WorkspaceId,
        Preferences = new Dictionary<string, string>(state.Preferences),
        TopicFrequency = new Dictionary<string, int>(state.TopicFrequency),
        DomainContext = new Dictionary<string, string>(state.DomainContext),
        FirstSeenAt = state.FirstSeenAt,
        LastSeenAt = state.LastSeenAt,
        TotalInteractions = state.TotalInteractions
    };
}
