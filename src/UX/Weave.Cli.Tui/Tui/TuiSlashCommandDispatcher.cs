using Spectre.Console;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tui;

internal sealed class TuiSlashCommandDispatcher
{
    private readonly TuiChatSession _chatSession;
    private readonly Dictionary<string, ITuiVerb> _byName;

    public TuiSlashCommandDispatcher(TuiChatSession chatSession, IEnumerable<ITuiVerb> verbs)
    {
        _chatSession = chatSession;
        _byName = new Dictionary<string, ITuiVerb>(StringComparer.Ordinal);
        foreach (var verb in verbs)
        {
            _byName[verb.Name] = verb;
            foreach (var alias in verb.Aliases)
                _byName[alias] = verb;
        }
    }

    public async Task<TuiDispatchResult> DispatchAsync(
        TuiSession session,
        string raw,
        CancellationToken ct)
    {
        var (name, args) = TuiCommandParser.Parse(raw);

        // Built-in meta verbs touch dispatcher-level state directly and don't
        // fit the ITuiVerb shape (no session input or no continuation).
        switch (name)
        {
            case "":
                return TuiDispatchResult.Continue;

            case "help":
            case "?":
                TuiHelpView.Render();
                return TuiDispatchResult.Continue;

            case "quit":
            case "exit":
            case "q":
                return TuiDispatchResult.Quit;

            case "clear":
            case "cls":
                AnsiConsole.Clear();
                CliTheme.WriteBanner();
                return TuiDispatchResult.Continue;

            case "history":
                _chatSession.ShowHistory(session);
                return TuiDispatchResult.Continue;

            case "new":
            case "n":
                TuiNewWorkspaceHint.Show();
                return TuiDispatchResult.Continue;
        }

        if (_byName.TryGetValue(name, out var verb))
        {
            await verb.DispatchAsync(new TuiVerbContext(session, args, _chatSession.Clear), ct);
            return TuiDispatchResult.Continue;
        }

        WriteUnknownCommand(name);
        return TuiDispatchResult.Continue;
    }

    private static void WriteUnknownCommand(string name)
    {
        var suggestion = TuiCommandParser.Suggest(name);
        if (suggestion is not null)
            CliTheme.WriteError($"Unknown command: /{name}. Did you mean /{suggestion}?  Type /help for all commands.");
        else
            CliTheme.WriteError($"Unknown command: /{name}. Type /help for all commands.");
    }
}
