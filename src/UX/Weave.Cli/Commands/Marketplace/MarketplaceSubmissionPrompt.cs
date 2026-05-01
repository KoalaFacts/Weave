using Spectre.Console;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class MarketplaceSubmissionPrompt
{
    public MarketplaceSubmission Prompt()
    {
        var name = AnsiConsole.Prompt(new TextPrompt<string>("Item name:").Styled());
        var description = AnsiConsole.Prompt(new TextPrompt<string>("Description:").Styled());
        var category = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Category:")
                .Styled()
                .AddChoices("ToolConnector", "AgentSkill", "ToolChain", "Integration"));
        var version = AnsiConsole.Prompt(new TextPrompt<string>("Version:").Styled().DefaultValue("1.0.0"));
        var author = AnsiConsole.Prompt(new TextPrompt<string>("Author:").Styled());
        var tagsInput = AnsiConsole.Prompt(new TextPrompt<string>("Tags (comma-separated):").Styled().AllowEmpty());

        var tags = string.IsNullOrWhiteSpace(tagsInput)
            ? Array.Empty<string>()
            : tagsInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new MarketplaceSubmission(name, description, category, version, author, tags);
    }
}
