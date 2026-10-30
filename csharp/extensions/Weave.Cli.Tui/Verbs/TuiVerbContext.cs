namespace Weave.Cli.Tui.Verbs;

internal sealed record TuiVerbContext(
    TuiSession Session,
    string? Args,
    Action ClearChatHistory);
