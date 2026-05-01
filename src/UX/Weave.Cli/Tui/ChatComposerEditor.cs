using System.Text;

namespace Weave.Cli.Tui;

internal sealed class ChatComposerEditor
{
    private readonly ChatTextBuffer _text = new();
    private readonly ChatInputHistory _history = new();
    private readonly ChatExitConfirmation _exit = new();
    private readonly ChatCommandMenu _menu = new();
    private readonly ChatComposerKeyHandler _keyHandler;

    internal ChatComposerEditor()
    {
        _keyHandler = new ChatComposerKeyHandler(_text, _history, _exit, _menu);
    }

    internal ChatTextBuffer TextBuffer => _text;
    internal StringBuilder Buffer => _text.Buffer;
    internal int CursorPosition { get => _text.CursorPosition; set => _text.CursorPosition = value; }
    internal bool ExitRequested => _exit.ExitRequested;
    internal string Text => _text.Text;
    internal int MenuSelectedIndex => _menu.SelectedIndex;
    internal bool IsExitArmed => _exit.IsArmed;
    internal bool HasExpiredExitHint => _exit.HasExpiredHint;

    internal void BeginRead(TuiSession session)
    {
        _text.Clear();
        _history.BeginRead();
        _keyHandler.Session = session;
        _exit.Reset();
        InvalidateMenuCache();
    }

    internal void EndRead()
    {
        _keyHandler.Session = null;
    }

    internal void ExpireExitHint()
    {
        _exit.ExpireHint();
    }

    internal void RecordSubmittedText(string text)
    {
        _history.Record(text);
    }

    internal void NormalizeMenuSelection(IReadOnlyList<(string Name, string Desc)> matches)
    {
        _menu.Normalize(matches);
    }

    internal bool HandleKey(ConsoleKeyInfo key, out bool submitted)
    {
        return _keyHandler.HandleKey(key, out submitted);
    }

    internal List<(string Name, string Desc)> MenuMatches()
    {
        return _keyHandler.MenuMatches();
    }

    internal bool IsMenuActive() => _keyHandler.IsMenuActive();

    internal void AcceptCompletion(string name)
    {
        _keyHandler.AcceptCompletion(name);
    }

    private void InvalidateMenuCache()
    {
        _menu.Invalidate();
    }
}
