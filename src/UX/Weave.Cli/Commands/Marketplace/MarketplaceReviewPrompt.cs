using Spectre.Console;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class MarketplaceReviewPrompt
{
    public MarketplaceReview Prompt()
    {
        var reviewerId = AnsiConsole.Prompt(new TextPrompt<string>("Reviewer ID:").Styled());
        var approved = AnsiConsole.Confirm("Approve for publishing?");
        var notes = AnsiConsole.Prompt(new TextPrompt<string>("Review notes (optional):").Styled().AllowEmpty());

        return new MarketplaceReview(
            reviewerId,
            approved,
            string.IsNullOrWhiteSpace(notes) ? null : notes);
    }
}
