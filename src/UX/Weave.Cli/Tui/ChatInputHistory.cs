namespace Weave.Cli.Tui;

internal sealed class ChatInputHistory
{
    private readonly List<string> _entries = [];
    private int _index;
    private const int MaxEntries = 50;

    internal void BeginRead()
    {
        _index = _entries.Count;
    }

    internal void Record(string text)
    {
        if (text.Length == 0)
            return;

        if (_entries.Count >= MaxEntries)
            _entries.RemoveAt(0);
        _entries.Add(text);
    }

    internal bool RecallPrevious(ChatTextBuffer text)
    {
        if (_entries.Count == 0 || _index == 0)
            return false;

        _index--;
        text.Replace(_entries[_index]);
        return true;
    }

    internal bool RecallNext(ChatTextBuffer text)
    {
        if (_entries.Count == 0 || _index >= _entries.Count)
            return false;

        _index++;
        text.Clear();
        if (_index < _entries.Count)
            text.Replace(_entries[_index]);
        return true;
    }
}
