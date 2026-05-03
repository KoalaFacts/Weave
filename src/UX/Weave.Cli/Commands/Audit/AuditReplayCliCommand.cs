using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class AuditReplayCliCommand : ICliCommand<AuditReplayOptions>
{
    public string Name => "replay";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Replay every authorization decision (allow + deny) for a capability token.";

    public async Task<int> ExecuteAsync(AuditReplayOptions options, CancellationToken ct)
    {
        using var client = new AuditApiClient();

        var tokenId = options.TokenId;
        if (string.IsNullOrWhiteSpace(tokenId))
        {
            tokenId = await PromptForTokenIdAsync(client, options.Limit, ct);
            if (string.IsNullOrWhiteSpace(tokenId))
                return 0;
        }

        IReadOnlyList<ApiCapabilityAuditEntry> rows;
        try
        {
            rows = await client.GetByTokenAsync(tokenId, ct);
        }
        catch (HttpRequestException ex)
        {
            CliTheme.WriteError($"Failed to query the silo: {ex.Message}");
            return 1;
        }

        if (rows.Count == 0)
        {
            CliTheme.WriteWarning($"No audit rows recorded for token '{tokenId}'.");
            CliTheme.WriteMuted("Either the token never authorized through this silo, or its rows have been evicted (capacity bound).");
            return 0;
        }

        RenderTable(tokenId, rows);
        return 0;
    }

    private static async Task<string?> PromptForTokenIdAsync(AuditApiClient client, int limit, CancellationToken ct)
    {
        IReadOnlyList<ApiCapabilityAuditEntry> recent;
        try
        {
            recent = await client.GetRecentAsync(limit, ct);
        }
        catch (HttpRequestException ex)
        {
            CliTheme.WriteError($"Failed to query the silo: {ex.Message}");
            return null;
        }

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

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Pick a capability token to replay:")
                .AddChoices(distinctTokens.Select(t => $"{t.TokenId}  ({t.IssuedTo} @ {t.Workspace}, {t.Count} rows)")));

        return distinctTokens
            .First(t => selection.StartsWith(t.TokenId, StringComparison.Ordinal))
            .TokenId;
    }

    private static void RenderTable(string tokenId, IReadOnlyList<ApiCapabilityAuditEntry> rows)
    {
        var table = CliTheme.CreateTable($"Capability replay — {tokenId}");
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
        CliTheme.WriteMuted($"Token issued to {first.IssuedTo} in workspace {first.WorkspaceId}.");
        CliTheme.WriteMuted($"{rows.Count} row(s).");
    }
}
