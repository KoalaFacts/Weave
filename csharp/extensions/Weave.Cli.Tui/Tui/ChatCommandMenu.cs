using System.Globalization;

namespace Weave.Cli.Tui;

internal sealed class ChatCommandMenu
{
    private static readonly CommandEntry[] Commands =
    [
        new("help",    "Show help grouped by task",              _ => true),
        new("open",    "Open a workspace for this session",      _ => true),
        new("up",      "Start the current workspace",            s => s.HasWorkspace && !s.IsRunning),
        new("down",    "Stop the current workspace",             s => s.IsRunning),
        new("use",     "Pick the agent that receives messages",  s => s.HasWorkspace),
        new("agents",  "List agents in the current workspace",   s => s.HasWorkspace),
        new("watch",   "Live-refresh the current workspace",     s => s.IsRunning),
        new("tools",   "List tools in the running workspace",    s => s.IsRunning),
        new("tasks",   "List tasks for the active agent",        s => s.IsRunning && s.AgentName is not null),
        new("history", "Show recent conversation messages",      s => s.AgentName is not null),
        new("status",  "Show workspace status",                  s => s.HasWorkspace),
        new("validate","Validate the workspace manifest",        s => s.HasWorkspace),
        new("ports",   "Show port assignments",                  _ => true),
        new("config",  "View CLI configuration",                 _ => true),
        new("refresh", "Re-render the dashboard",                _ => true),
        new("new",     "Hints for creating a workspace",         _ => true),
        new("presets", "Built-in workspace presets",             _ => true),
        new("webui",   "Open the web dashboard in a browser",    _ => true),
        new("system",  "Silo and CLI config info",               _ => true),
        new("version", "Installed version + update info",        _ => true),
        new("upgrade", "Check NuGet for a newer release",        _ => true),
        new("clear",   "Clear the screen",                       _ => true),
        new("quit",    "Exit the TUI",                           _ => true),
    ];

    private string _cachedKey = string.Empty;
    private List<(string Name, string Desc)> _cachedResult = [];

    internal int SelectedIndex { get; private set; }

    internal void Invalidate()
    {
        _cachedKey = string.Empty;
    }

    internal List<(string Name, string Desc)> Matches(string text, int cursor, TuiSession? session)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{text}|{cursor}");
        if (key == _cachedKey)
            return _cachedResult;

        _cachedKey = key;
        _cachedResult = Match(text, cursor, session);
        return _cachedResult;
    }

    internal void Normalize(IReadOnlyList<(string Name, string Desc)> matches)
    {
        if (matches.Count == 0)
            SelectedIndex = 0;
        else if (SelectedIndex >= matches.Count)
            SelectedIndex = matches.Count - 1;
    }

    internal void SelectPrevious(IReadOnlyList<(string Name, string Desc)> matches)
    {
        SelectedIndex = (SelectedIndex - 1 + matches.Count) % matches.Count;
    }

    internal void SelectNext(IReadOnlyList<(string Name, string Desc)> matches)
    {
        SelectedIndex = (SelectedIndex + 1) % matches.Count;
    }

    internal void ResetSelection()
    {
        SelectedIndex = 0;
    }

    private static List<(string Name, string Desc)> Match(string text, int cursor, TuiSession? session)
    {
        if (text.Length == 0 || text[0] != '/')
            return [];

        var space = text.IndexOf(' ');
        var cursorInCommandWord = space < 0 || cursor <= space;
        if (!cursorInCommandWord)
            return [];

        var prefix = space < 0 ? text[1..] : text[1..space];

        var available = Commands
            .Where(command => session is null || command.Available(session));

        var filtered = prefix.Length == 0
            ? available
            : available.Where(command => command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return [.. filtered.Select(command => (command.Name, command.Desc))];
    }

    private sealed record CommandEntry(string Name, string Desc, Func<TuiSession, bool> Available);
}
