using System.Text;
using Weave.Shared.Ids;

using Weave.Agents.Chat;
using Weave.Agents.Lifecycle;
using Weave.Agents.Verification;
namespace Weave.Agents.Memory;

internal static class EpisodeExtractor
{
    private const int MaxTitleLength = 100;
    private const int MaxMessageLength = 500;
    private const int MaxDecisionValueLength = 200;

    public static Episode? FromTask(AgentTaskInfo task, AgentState state, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(task.Description))
            return null;

        var historySlice = state.History.Skip(state.LastEpisodeHistoryIndex).ToList();

        return new Episode
        {
            EpisodeId = EpisodeId.New(),
            Title = Truncate(task.Description, MaxTitleLength),
            Narrative = BuildNarrative(historySlice, fallback: task.Description),
            AgentName = state.AgentName,
            Tags = BuildTags(task, state),
            Decisions = BuildDecisionsFromProof(task.Proof),
            SourceTaskId = task.TaskId.ToString(),
            ReviewFeedback = task.Proof?.ReviewFeedback,
            OccurredAt = occurredAt
        };
    }

    public static Episode? FromSession(AgentState state, DateTimeOffset occurredAt)
    {
        var historySlice = state.History.Skip(state.LastEpisodeHistoryIndex).ToList();
        if (historySlice.Count == 0)
            return null;

        var firstUser = historySlice.FirstOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        var rawTitle = (firstUser?.Content ?? historySlice[0].Content).Trim();
        var title = rawTitle.Length == 0
            ? $"Session with {state.AgentName}"
            : Truncate(rawTitle, MaxTitleLength);

        return new Episode
        {
            EpisodeId = EpisodeId.New(),
            Title = title,
            Narrative = BuildNarrative(historySlice, fallback: title),
            AgentName = state.AgentName,
            Tags = [state.AgentName],
            Decisions = [],
            SourceTaskId = null,
            ReviewFeedback = null,
            OccurredAt = occurredAt
        };
    }

    private static string BuildNarrative(List<ConversationMessage> messages, string fallback)
    {
        if (messages.Count == 0)
            return fallback;

        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            var content = Truncate(message.Content, MaxMessageLength);
            if (string.IsNullOrWhiteSpace(content))
                continue;
            builder.Append(message.Role).Append(": ").AppendLine(content);
        }

        var narrative = builder.ToString().TrimEnd();
        return narrative.Length == 0 ? fallback : narrative;
    }

    private static List<EpisodeDecision> BuildDecisionsFromProof(ProofOfWork? proof)
    {
        if (proof is null || proof.Items.Count == 0)
            return [];

        var decisionTypes = new[] { ProofType.PullRequest, ProofType.CodeReview, ProofType.Custom };
        return proof.Items
            .Where(p => decisionTypes.Contains(p.Type))
            .Select(p => new EpisodeDecision
            {
                Question = $"{p.Type}: {p.Label}",
                ChosenOption = Truncate(p.Value, MaxDecisionValueLength)
            })
            .ToList();
    }

    private static List<string> BuildTags(AgentTaskInfo task, AgentState state)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state.AgentName };
        if (task.Proof is not null)
        {
            foreach (var item in task.Proof.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Label))
                    tags.Add(item.Label);
            }
        }
        return tags.ToList();
    }

    private static string Truncate(string value, int max) =>
        value.Length > max ? value[..max] : value;
}
