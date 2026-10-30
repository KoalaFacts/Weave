using System.Globalization;
using System.Text;

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

    /// <summary>
    /// Records an interaction with the user, applying eviction (capacity-bounded
    /// recents), per-topic frequency counting, and first/last-seen bookkeeping.
    /// Multi-field coupling lives here so the actor stays a thin orchestrator.
    /// </summary>
    public void RecordInteraction(InteractionRecord record, DateTimeOffset now)
    {
        if (RecentInteractions.Count >= MaxRecentInteractions)
            RecentInteractions.RemoveAt(0);

        RecentInteractions.Add(record);

        foreach (var topic in record.Topics)
        {
            if (TopicFrequency.TryGetValue(topic, out var count))
                TopicFrequency[topic] = count + 1;
            else
                TopicFrequency[topic] = 1;
        }

        TotalInteractions++;
        FirstSeenAt ??= now;
        LastSeenAt = now;
    }

    /// <summary>
    /// Returns a single-line context summary spanning preferences, top-5 topics
    /// by frequency, and domain context. Empty profiles return the empty string.
    /// </summary>
    public string BuildContextSummary()
    {
        if (TotalInteractions == 0
            && Preferences.Count == 0
            && DomainContext.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        if (Preferences.Count > 0)
        {
            sb.Append("User preferences: ");
            sb.Append(string.Join(", ", Preferences.Select(kv => $"{kv.Key}={kv.Value}")));
            sb.Append(". ");
        }

        if (TopicFrequency.Count > 0)
        {
            var topTopics = TopicFrequency
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key} ({kv.Value})");

            sb.Append("Top topics: ");
            sb.Append(string.Join(", ", topTopics));
            sb.Append(". ");
        }

        if (DomainContext.Count > 0)
        {
            sb.Append("Domain context: ");
            sb.Append(string.Join(", ", DomainContext.Select(kv => $"{kv.Key}={kv.Value}")));
            sb.Append(". ");
        }

        sb.Append(CultureInfo.InvariantCulture, $"Interactions: {TotalInteractions} total.");
        return sb.ToString();
    }

    /// <summary>
    /// Resets the profile back to defaults (clears all dictionaries / lists,
    /// drops first/last-seen, restores <see cref="MaxRecentInteractions"/> to
    /// its default). The actor still owns persistence + authorization; this
    /// owns the field-set that "clear" actually means.
    /// </summary>
    public void Clear()
    {
        Preferences.Clear();
        RecentInteractions.Clear();
        TopicFrequency.Clear();
        DomainContext.Clear();
        TotalInteractions = 0;
        FirstSeenAt = null;
        LastSeenAt = null;
        PreferredModel = null;
        PreferredLanguage = null;
        MaxRecentInteractions = 100;
    }
}
