using System.Text;

namespace Weave.Cli.Tui;

internal sealed class ChatComposerEditor
{
    private readonly ChatTextBuffer _text = new();
    private readonly ChatInputHistory _history = new();
    private readonly ChatExitConfirmation _exit = new();
    private readonly ChatCommandMenu _menu = new();

    private TuiSession? _session;

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
        _session = session;
        _exit.Reset();
        InvalidateMenuCache();
    }

    internal void EndRead()
    {
        _session = null;
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
        submitted = false;

        if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            return _exit.Press();

        _exit.ExpireHint();

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if ((key.Modifiers & (ConsoleModifiers.Shift | ConsoleModifiers.Alt)) != 0)
                {
                    _text.Insert('\n');
                    InvalidateMenuCache();
                    return true;
                }
                if (_text.Length == 0)
                    return false;

                var enterMatches = MenuMatches();
                if (enterMatches.Count > 0)
                    AcceptCompletion(enterMatches[_menu.SelectedIndex].Name);

                submitted = true;
                return true;

            case ConsoleKey.Backspace:
                if (_text.Backspace())
                {
                    InvalidateMenuCache();
                    return true;
                }
                return false;

            case ConsoleKey.Delete:
                if (_text.Delete())
                {
                    InvalidateMenuCache();
                    return true;
                }
                return false;

            case ConsoleKey.LeftArrow:
                if (_text.MoveLeft())
                {
                    InvalidateMenuCache();
                    return true;
                }
                return false;

            case ConsoleKey.RightArrow:
                if (_text.MoveRight())
                {
                    InvalidateMenuCache();
                    return true;
                }
                return false;

            case ConsoleKey.Home:
                _text.MoveToLineStart();
                InvalidateMenuCache();
                return true;

            case ConsoleKey.End:
                _text.MoveToLineEnd();
                InvalidateMenuCache();
                return true;

            case ConsoleKey.UpArrow:
                if (IsMenuActive())
                {
                    var upMatches = MenuMatches();
                    _menu.SelectPrevious(upMatches);
                }
                else if (!_text.ContainsNewLine() && _history.RecallPrevious(_text))
                {
                    InvalidateMenuCache();
                }
                else
                {
                    _text.MoveLine(up: true);
                    InvalidateMenuCache();
                }
                return true;

            case ConsoleKey.DownArrow:
                if (IsMenuActive())
                {
                    var downMatches = MenuMatches();
                    _menu.SelectNext(downMatches);
                }
                else if (!_text.ContainsNewLine() && _history.RecallNext(_text))
                {
                    InvalidateMenuCache();
                }
                else
                {
                    _text.MoveLine(up: false);
                    InvalidateMenuCache();
                }
                return true;

            case ConsoleKey.Tab:
                {
                    var tabMatches = MenuMatches();
                    if (tabMatches.Count == 0)
                        return false;
                    AcceptCompletion(tabMatches[_menu.SelectedIndex].Name);
                    return true;
                }

            case ConsoleKey.Escape:
                if (_text.Length > 0)
                {
                    _text.Clear();
                    InvalidateMenuCache();
                    return true;
                }
                return false;
        }

        if (key.Key == ConsoleKey.W && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            if (_text.DeleteWordBeforeCursor())
            {
                InvalidateMenuCache();
                return true;
            }
            return false;
        }

        if (key.Key == ConsoleKey.U && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            if (_text.ClearToLineStart())
            {
                InvalidateMenuCache();
                return true;
            }
            return false;
        }

        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            _text.Insert(key.KeyChar);
            InvalidateMenuCache();
            return true;
        }

        return false;
    }

    internal List<(string Name, string Desc)> MenuMatches()
    {
        return _menu.Matches(_text.Text, _text.CursorPosition, _session);
    }

    internal bool IsMenuActive() => MenuMatches().Count > 0;

    internal void AcceptCompletion(string name)
    {
        _text.Replace($"/{name} ");
        _menu.ResetSelection();
        InvalidateMenuCache();
    }

    private void InvalidateMenuCache()
    {
        _menu.Invalidate();
    }
}
