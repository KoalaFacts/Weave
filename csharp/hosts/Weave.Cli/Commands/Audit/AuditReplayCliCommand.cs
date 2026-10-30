using Spectre.Console;
using Weave.Actions.Audit;
using Weave.Actions.Context;

namespace Weave.Cli.Commands;

internal sealed class AuditReplayCliCommand(
    GetCapabilityAuditByTokenAction getByTokenAction,
    GetRecentCapabilityAuditAction getRecentAction) : ICliCommand<AuditReplayOptions>
{
    public string Name => "replay";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Replay every authorization decision (allow + deny) for a capability token.";

    public async Task<int> ExecuteAsync(AuditReplayOptions options, CancellationToken ct)
    {
        var tokenId = options.TokenId;
        if (string.IsNullOrWhiteSpace(tokenId))
        {
            tokenId = await PromptForTokenIdAsync(options.Limit, ct);
            if (string.IsNullOrWhiteSpace(tokenId))
                return 0;
        }

        var result = await getByTokenAction.ExecuteAsync(new GetCapabilityAuditByTokenInput(tokenId), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;
            CliTheme.WriteError($"Failed to query the silo: {result.Failure.Message}");
            return 1;
        }

        if (result.Value.Entries.Count == 0)
        {
            CliTheme.WriteWarning($"No audit rows recorded for token '{tokenId}'.");
            CliTheme.WriteMuted("Either the token never authorized through this silo, or its rows have been evicted (capacity bound).");
            return 0;
        }

        RenderTable(tokenId, result.Value.Entries);
        return 0;
    }

    private async Task<string?> PromptForTokenIdAsync(int limit, CancellationToken ct)
    {
        var result = await getRecentAction.ExecuteAsync(new GetRecentCapabilityAuditInput(limit), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return null;
            CliTheme.WriteError($"Failed to query the silo: {result.Failure.Message}");
            return null;
        }

        var recent = result.Value.Entries;
        if (recent.Count == 0)
        {
            CliTheme.WriteWarning("No capability authorization rows on this silo yet.");
            return null;
        }

        var distinctTokens = recent
            .GroupBy(r => r.TokenId)
            .Select(g => new
            {
                TokenId = g.Key,
                IssuedTo = g.First().IssuedTo,
                Workspace = g.First().WorkspaceId,
                Count = g.Count()
            })
            .ToList();

        // Visible labels show only the token id prefix — keeps full ids out of
        // shoulder-surf range and reduces visual noise. The full id stays in
        // the lookup map so the user picks unambiguously.
        var labels = distinctTokens
            .ToDictionary(
                t => $"{ShortId(t.TokenId)}  ({t.IssuedTo} @ {t.Workspace}, {t.Count} rows)",
                t => t.TokenId);

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Pick a capability token to replay:")
                .AddChoices(labels.Keys));

        return labels[selection];
    }

    private static void RenderTable(string tokenId, IReadOnlyList<CapabilityAuditEntry> rows)
    {
        var table = CliTheme.CreateTable($"Capability replay — {ShortId(tokenId)}");
        table.AddColumn(CliTheme.StyledColumn("Time"));
        table.AddColumn(CliTheme.StyledColumn("Outcome"));
        table.AddColumn(CliTheme.StyledColumn("Grant"));
        table.AddColumn(CliTheme.StyledColumn("Action"));
        table.AddColumn(CliTheme.StyledColumn("Reason"));

        foreach (var row in rows)
        {
            var outcome = string.Equals(row.Outcome, "Deny", StringComparison.Ordinal)
                ? $"[red]{row.Outcome}[/]"
                : $"[green]{row.Outcome}[/]";

            table.AddRow(
                Markup.Escape(row.Timestamp.ToString("u")),
                outcome,
                Markup.Escape(row.Grant),
                Markup.Escape(row.ActionContext),
                row.Reason is null
                    ? $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]—[/]"
                    : Markup.Escape(row.Reason));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        var first = rows[0];
        CliTheme.WriteMuted($"Token {ShortId(tokenId)} issued to {first.IssuedTo} in workspace {first.WorkspaceId}.");
        CliTheme.WriteMuted($"{rows.Count} row(s).");
    }

    private static string ShortId(string tokenId) =>
        tokenId.Length <= 8 ? tokenId : $"{tokenId[..8]}…";
}
