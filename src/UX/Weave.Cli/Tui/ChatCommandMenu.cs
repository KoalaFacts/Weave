using System.Globalization;

namespace Weave.Cli.Tui;

internal sealed class ChatCommandMenu
{
    private readonly ChatCommandCatalog _commandCatalog = new();
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
        _cachedResult = _commandCatalog.Match(text, cursor, session);
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
}
