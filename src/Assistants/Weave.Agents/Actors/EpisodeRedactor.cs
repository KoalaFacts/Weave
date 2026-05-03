using Weave.Agents.Models;
using Weave.Security.Scanning;

namespace Weave.Agents.Actors;

internal sealed class EpisodeRedactor(ILeakScanner leakScanner)
{
    private const string RedactedMarker = "***REDACTED***";

    public async Task<Episode> RedactAsync(string workspaceId, Episode episode)
    {
        var scanContext = new ScanContext
        {
            WorkspaceId = workspaceId,
            SourceComponent = $"episodic-memory:{episode.AgentName}",
            Direction = ScanDirection.Inbound
        };

        var title = await RedactContentAsync(episode.Title, scanContext);
        var narrative = await RedactContentAsync(episode.Narrative, scanContext);
        var feedback = await RedactContentAsync(episode.ReviewFeedback, scanContext);

        return episode with
        {
            Title = title ?? episode.Title,
            Narrative = narrative ?? episode.Narrative,
            ReviewFeedback = feedback,
            Decisions = await RedactDecisionsAsync(episode.Decisions, scanContext)
        };
    }

    private async Task<List<EpisodeDecision>> RedactDecisionsAsync(
        List<EpisodeDecision> decisions,
        ScanContext scanContext)
    {
        var redacted = new List<EpisodeDecision>(decisions.Count);
        foreach (var decision in decisions)
        {
            redacted.Add(new EpisodeDecision
            {
                Question = await RedactContentAsync(decision.Question, scanContext) ?? decision.Question,
                ChosenOption = await RedactContentAsync(decision.ChosenOption, scanContext) ?? decision.ChosenOption
            });
        }

        return redacted;
    }

    private async Task<string?> RedactContentAsync(string? content, ScanContext context)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var result = await leakScanner.ScanStringAsync(content, context);
        return result.HasLeaks ? ApplyRedactions(content, result.Findings) : content;
    }

    private static string ApplyRedactions(string content, IReadOnlyList<LeakFinding> findings)
    {
        var ordered = findings
            .Where(f => f.Length > 0 && f.Offset >= 0 && f.Offset < content.Length)
            .OrderByDescending(f => f.Offset)
            .ToList();

        if (ordered.Count == 0)
            return content;

        var span = content.AsSpan();
        var builder = new System.Text.StringBuilder(content.Length);
        var cursor = content.Length;
        foreach (var finding in ordered)
        {
            var end = Math.Min(finding.Offset + finding.Length, cursor);
            if (end <= finding.Offset)
                continue;

            builder.Insert(0, span[end..cursor]);
            builder.Insert(0, RedactedMarker);
            cursor = finding.Offset;
        }

        builder.Insert(0, span[..cursor]);
        return builder.ToString();
    }
}
