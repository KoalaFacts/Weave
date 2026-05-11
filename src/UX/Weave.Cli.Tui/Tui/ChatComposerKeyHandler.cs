namespace Weave.Cli.Tui;

internal sealed class ChatComposerKeyHandler(
    ChatTextBuffer text,
    ChatInputHistory history,
    ChatExitConfirmation exit,
    ChatCommandMenu menu)
{
    internal TuiSession? Session { get; set; }

    internal bool HandleKey(ConsoleKeyInfo key, out bool submitted)
    {
        submitted = false;

        if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            return exit.Press();

        exit.ExpireHint();

        return key.Key switch
        {
            ConsoleKey.Enter => HandleEnter(key, out submitted),
            ConsoleKey.Backspace => Edit(text.Backspace),
            ConsoleKey.Delete => Edit(text.Delete),
            ConsoleKey.LeftArrow => Edit(text.MoveLeft),
            ConsoleKey.RightArrow => Edit(text.MoveRight),
            ConsoleKey.Home => MoveToLineBoundary(text.MoveToLineStart),
            ConsoleKey.End => MoveToLineBoundary(text.MoveToLineEnd),
            ConsoleKey.UpArrow => HandleUpArrow(),
            ConsoleKey.DownArrow => HandleDownArrow(),
            ConsoleKey.Tab => HandleTab(),
            ConsoleKey.Escape => HandleEscape(),
            _ => HandleTextKey(key)
        };
    }

    internal List<(string Name, string Desc)> MenuMatches()
    {
        return menu.Matches(text.Text, text.CursorPosition, Session);
    }

    internal bool IsMenuActive() => MenuMatches().Count > 0;

    internal void AcceptCompletion(string name)
    {
        text.Replace($"/{name} ");
        menu.ResetSelection();
        InvalidateMenuCache();
    }

    private bool HandleEnter(ConsoleKeyInfo key, out bool submitted)
    {
        submitted = false;

        if ((key.Modifiers & (ConsoleModifiers.Shift | ConsoleModifiers.Alt)) != 0)
            return Edit(() => text.Insert('\n'));
        if (text.Length == 0)
            return false;

        var matches = MenuMatches();
        if (matches.Count > 0)
            AcceptCompletion(matches[menu.SelectedIndex].Name);

        submitted = true;
        return true;
    }

    private bool HandleUpArrow()
    {
        if (IsMenuActive())
        {
            menu.SelectPrevious(MenuMatches());
            return true;
        }

        if (!text.ContainsNewLine() && history.RecallPrevious(text))
            return InvalidateAndReturn();

        return MoveAcrossLines(up: true);
    }

    private bool HandleDownArrow()
    {
        if (IsMenuActive())
        {
            menu.SelectNext(MenuMatches());
            return true;
        }

        if (!text.ContainsNewLine() && history.RecallNext(text))
            return InvalidateAndReturn();

        return MoveAcrossLines(up: false);
    }

    private bool HandleTab()
    {
        var matches = MenuMatches();
        if (matches.Count == 0)
            return false;

        AcceptCompletion(matches[menu.SelectedIndex].Name);
        return true;
    }

    private bool HandleEscape()
    {
        if (text.Length == 0)
            return false;

        text.Clear();
        return InvalidateAndReturn();
    }

    private bool HandleTextKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.W && (key.Modifiers & ConsoleModifiers.Control) != 0)
            return Edit(text.DeleteWordBeforeCursor);

        if (key.Key == ConsoleKey.U && (key.Modifiers & ConsoleModifiers.Control) != 0)
            return Edit(text.ClearToLineStart);

        if (key.KeyChar == '\0' || char.IsControl(key.KeyChar))
            return false;

        return Edit(() => text.Insert(key.KeyChar));
    }

    private bool MoveAcrossLines(bool up) => Edit(() => text.MoveLine(up));

    private bool MoveToLineBoundary(Func<bool> move)
    {
        move();
        return InvalidateAndReturn();
    }

    private bool Edit(Func<bool> edit)
    {
        if (!edit())
            return false;

        return InvalidateAndReturn();
    }

    private bool InvalidateAndReturn()
    {
        InvalidateMenuCache();
        return true;
    }

    private void InvalidateMenuCache()
    {
        menu.Invalidate();
    }
}
