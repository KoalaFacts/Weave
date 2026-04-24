using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

/// <summary>
/// Multi-line chat composer rendered inside a rounded panel, with a
/// context-aware footer (workspace · agent · mode · Ctrl+C hint).
///
/// Input is hand-rolled on <see cref="Console.ReadKey(bool)"/> rather
/// than <c>Spectre.Console.TextPrompt</c> so we can: show a custom
/// cursor, repaint the panel on every keystroke, and support
/// Shift+Enter for newlines.
/// </summary>
internal sealed class ChatComposer
{
    private readonly StringBuilder _buffer = new();
    private int _cursor;
    private bool _cursorOn = true;

    // Test helpers — allow setting up buffer state without ReadAsync.
    internal StringBuilder Buffer => _buffer;
    internal int CursorPosition { get => _cursor; set => _cursor = value; }
    internal bool ExitRequested => _exitRequested;

    // Poll loop ticks every 25 ms; 20 ticks ≈ 500 ms → classic
    // terminal blink rate. Counter resets on each keystroke so the
    // cursor stays solid while the user is actively typing.
    private const int BlinkTicks = 20;
    private int _blinkCounter;

    // Slash-command autocomplete selection index — bounds-checked
    // every render against the filtered list.
    private int _menuSelectedIndex;

    // Input history for up/down arrow recall.
    private readonly List<string> _inputHistory = [];
    private int _historyIndex;
    private const int MaxHistoryEntries = 50;

    // Double-Ctrl+C to exit: first press primes a 2 s window during
    // which a second press exits. Prevents accidental loss of work
    // if the user reflexively hits Ctrl+C.
    private static readonly TimeSpan ExitConfirmWindow = TimeSpan.FromSeconds(2);
    private DateTime? _exitHintUntil;
    private bool _exitRequested;

    // Cache for MenuMatches() — invalidated when buffer or cursor changes.
    private string _cachedMenuKey = string.Empty;
    private List<(string Name, string Desc)> _cachedMenuResult = [];

    /// <summary>
    /// Commands shown in the autocomplete popup. Each entry has an
    /// <c>Available</c> predicate over the current <see cref="TuiSession"/>
    /// so the menu hides commands that don't apply right now (e.g.
    /// <c>/use</c> before a workspace is open, <c>/watch</c> before
    /// the workspace is running). Keep this in sync with the
    /// dispatcher in TuiApp.HandleSlashAsync.
    /// </summary>
    private static readonly CommandEntry[] CommandCatalog =
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

    private sealed record CommandEntry(string Name, string Desc, Func<TuiSession, bool> Available);

    // Session reference held only while ReadAsync is in flight so the
    // availability predicates can see the current workspace/agent.
    private TuiSession? _session;

    public string Placeholder { get; set; } = "Send a message, or / for commands…";

    public async Task<ComposerResult> ReadAsync(TuiSession session, CancellationToken ct)
    {
        _buffer.Clear();
        _cursor = 0;
        _historyIndex = _inputHistory.Count;

        var status = ComposerStatus.Cancelled;
        var cursorRestore = TryHideTerminalCursor();
        var ctrlCRestore = TryCaptureCtrlC();
        _session = session;
        _exitHintUntil = null;
        _exitRequested = false;

        try
        {
            await AnsiConsole.Live(BuildRenderable(session, focused: true))
                .AutoClear(true)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    ctx.UpdateTarget(BuildRenderable(session, focused: true));

                    while (!ct.IsCancellationRequested)
                    {
                        while (!KeyAvailable() && !ct.IsCancellationRequested)
                        {
                            try
                            { await Task.Delay(25, ct); }
                            catch (OperationCanceledException) { break; }

                            // Expire the "press Ctrl+C again" hint.
                            if (_exitHintUntil is { } deadline && DateTime.UtcNow >= deadline)
                            {
                                _exitHintUntil = null;
                                ctx.UpdateTarget(BuildRenderable(session, focused: true));
                            }

                            // Idle blink — only while waiting for input.
                            _blinkCounter++;
                            if (_blinkCounter >= BlinkTicks)
                            {
                                _blinkCounter = 0;
                                _cursorOn = !_cursorOn;
                                ctx.UpdateTarget(BuildRenderable(session, focused: true));
                            }
                        }
                        if (ct.IsCancellationRequested)
                            break;

                        var key = Console.ReadKey(intercept: true);

                        if (HandleKey(key, out var submitted))
                        {
                            if (_exitRequested)
                            {
                                status = ComposerStatus.Cancelled;
                                return;
                            }

                            if (submitted)
                            {
                                var text = _buffer.ToString().Trim();
                                if (text.Length > 0)
                                {
                                    if (_inputHistory.Count >= MaxHistoryEntries)
                                        _inputHistory.RemoveAt(0);
                                    _inputHistory.Add(text);
                                }
                                status = ComposerStatus.Submitted;
                                return;
                            }

                            // User activity: cursor solid, blink resets.
                            _cursorOn = true;
                            _blinkCounter = 0;
                            ctx.UpdateTarget(BuildRenderable(session, focused: true));
                        }
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // expected on Ctrl+C
        }
        finally
        {
            cursorRestore();
            ctrlCRestore();
            _session = null;
        }

        return new ComposerResult(status, _buffer.ToString());
    }

    /// <summary>
    /// Captures Ctrl+C as a regular key event (instead of triggering
    /// the global CancelKeyPress handler) so the composer can
    /// implement double-press-to-exit. Returned action restores the
    /// prior setting — after the composer closes, Ctrl+C during a
    /// chat call still cancels normally via the token.
    /// </summary>
    private static Action TryCaptureCtrlC()
    {
        bool previous = false;
        try
        { previous = Console.TreatControlCAsInput; }
        catch (Exception) { /* platform quirk */ }
        try
        { Console.TreatControlCAsInput = true; }
        catch (Exception) { /* ignore */ }

        return () =>
        {
            try
            { Console.TreatControlCAsInput = previous; }
            catch (Exception) { /* ignore */ }
        };
    }

    /// <summary>
    /// Hides the terminal's blinking text cursor for the composer
    /// session (otherwise it flashes over our custom glyph). Returns
    /// a restore action that's safe to call in a <c>finally</c>.
    /// The getter for <see cref="Console.CursorVisible"/> is Windows-
    /// only, so we don't read the previous state — default back to
    /// visible on exit.
    /// </summary>
    private static Action TryHideTerminalCursor()
    {
        try
        { Console.CursorVisible = false; }
        catch (IOException) { /* redirected stdout etc. — ignore */ }

        return () =>
        {
            try
            { Console.CursorVisible = true; }
            catch (IOException) { /* ignore */ }
        };
    }

    /// <summary>
    /// Applies a single keystroke to the buffer. Returns whether the
    /// view needs to be repainted; <paramref name="submitted"/> is set
    /// when the user pressed Enter with non-empty content.
    /// </summary>
    internal bool HandleKey(ConsoleKeyInfo key, out bool submitted)
    {
        submitted = false;

        // Double-Ctrl+C to exit. First press arms a 2 s window and
        // shows a hint; second press within the window exits.
        if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            var now = DateTime.UtcNow;
            if (_exitHintUntil is { } deadline && now < deadline)
            {
                _exitRequested = true;
                return true;
            }
            _exitHintUntil = now.Add(ExitConfirmWindow);
            return true;
        }

        // Any other key clears the exit-arming state.
        if (_exitHintUntil is not null)
            _exitHintUntil = null;

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if ((key.Modifiers & (ConsoleModifiers.Shift | ConsoleModifiers.Alt)) != 0)
                {
                    _buffer.Insert(_cursor, '\n');
                    _cursor++;
                    return true;
                }
                if (_buffer.Length == 0)
                    return false;

                // If the autocomplete popup is open, Enter accepts the
                // highlighted command and submits in one go. Otherwise
                // users who typed just "/" (or a partial prefix they
                // arrow-selected) would see nothing happen.
                var enterMatches = MenuMatches();
                if (enterMatches.Count > 0)
                    AcceptCompletion(enterMatches[_menuSelectedIndex].Name);

                submitted = true;
                return true;

            case ConsoleKey.Backspace:
                if (_cursor > 0)
                {
                    _buffer.Remove(_cursor - 1, 1);
                    _cursor--;
                    return true;
                }
                return false;

            case ConsoleKey.Delete:
                if (_cursor < _buffer.Length)
                {
                    _buffer.Remove(_cursor, 1);
                    return true;
                }
                return false;

            case ConsoleKey.LeftArrow:
                if (_cursor > 0)
                { _cursor--; return true; }
                return false;

            case ConsoleKey.RightArrow:
                if (_cursor < _buffer.Length)
                { _cursor++; return true; }
                return false;

            case ConsoleKey.Home:
                _cursor = LineStart(_cursor);
                return true;

            case ConsoleKey.End:
                _cursor = LineEnd(_cursor);
                return true;

            case ConsoleKey.UpArrow:
                if (IsMenuActive())
                {
                    var upMatches = MenuMatches();
                    _menuSelectedIndex = (_menuSelectedIndex - 1 + upMatches.Count) % upMatches.Count;
                }
                else if (!_buffer.ToString().Contains('\n') && _inputHistory.Count > 0 && _historyIndex > 0)
                {
                    _historyIndex--;
                    _buffer.Clear();
                    _buffer.Append(_inputHistory[_historyIndex]);
                    _cursor = _buffer.Length;
                }
                else
                {
                    MoveCursorLine(up: true);
                }
                return true;

            case ConsoleKey.DownArrow:
                if (IsMenuActive())
                {
                    var downMatches = MenuMatches();
                    _menuSelectedIndex = (_menuSelectedIndex + 1) % downMatches.Count;
                }
                else if (!_buffer.ToString().Contains('\n') && _inputHistory.Count > 0 && _historyIndex < _inputHistory.Count)
                {
                    _historyIndex++;
                    _buffer.Clear();
                    if (_historyIndex < _inputHistory.Count)
                        _buffer.Append(_inputHistory[_historyIndex]);
                    _cursor = _buffer.Length;
                }
                else
                {
                    MoveCursorLine(up: false);
                }
                return true;

            case ConsoleKey.Tab:
                {
                    var tabMatches = MenuMatches();
                    if (tabMatches.Count == 0)
                        return false;
                    AcceptCompletion(tabMatches[_menuSelectedIndex].Name);
                    return true;
                }

            case ConsoleKey.Escape:
                if (_buffer.Length > 0)
                {
                    _buffer.Clear();
                    _cursor = 0;
                    return true;
                }
                return false;
        }

        // Ctrl+W: delete word before cursor
        if (key.Key == ConsoleKey.W && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            if (_cursor > 0)
            {
                var s = _buffer.ToString();
                var end = _cursor;
                // skip whitespace before cursor
                while (_cursor > 0 && char.IsWhiteSpace(s[_cursor - 1]))
                    _cursor--;
                // skip word characters
                while (_cursor > 0 && !char.IsWhiteSpace(s[_cursor - 1]))
                    _cursor--;
                _buffer.Remove(_cursor, end - _cursor);
                return true;
            }
            return false;
        }

        // Ctrl+U: clear from cursor to start of line
        if (key.Key == ConsoleKey.U && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            if (_cursor > 0)
            {
                var start = LineStart(_cursor);
                _buffer.Remove(start, _cursor - start);
                _cursor = start;
                return true;
            }
            return false;
        }

        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            _buffer.Insert(_cursor, key.KeyChar);
            _cursor++;
            return true;
        }

        return false;
    }

    internal int LineStart(int from)
    {
        var s = _buffer.ToString();
        var i = from - 1;
        while (i >= 0 && s[i] != '\n')
            i--;
        return i + 1;
    }

    internal int LineEnd(int from)
    {
        var s = _buffer.ToString();
        var i = from;
        while (i < s.Length && s[i] != '\n')
            i++;
        return i;
    }

    private void MoveCursorLine(bool up)
    {
        var start = LineStart(_cursor);
        var column = _cursor - start;

        if (up)
        {
            if (start == 0)
                return;
            var prevEnd = start - 1;
            var prevStart = LineStart(prevEnd);
            var prevLen = prevEnd - prevStart;
            _cursor = prevStart + Math.Min(column, prevLen);
        }
        else
        {
            var end = LineEnd(_cursor);
            if (end >= _buffer.Length)
                return;
            var nextStart = end + 1;
            var nextEnd = LineEnd(nextStart);
            var nextLen = nextEnd - nextStart;
            _cursor = nextStart + Math.Min(column, nextLen);
        }
    }

    private Panel BuildRenderable(TuiSession session, bool focused)
    {
        var matches = MenuMatches();
        if (matches.Count == 0)
            _menuSelectedIndex = 0;
        else if (_menuSelectedIndex >= matches.Count)
            _menuSelectedIndex = matches.Count - 1;

        var children = new List<IRenderable>();
        if (matches.Count > 0)
        {
            children.Add(BuildMenu(matches));
            children.Add(new Markup(string.Empty));
        }
        children.Add(BuildInputMarkup(focused));
        children.Add(BuildDashedDivider());
        var exitArmed = _exitHintUntil is { } d && DateTime.UtcNow < d;
        children.Add(BuildFooter(session, menuActive: matches.Count > 0, exitArmed));

        var border = focused ? CliTheme.Accent : CliTheme.Muted;

        return new Panel(new Rows(children))
            .Border(BoxBorder.Rounded)
            .BorderColor(border)
            .Padding(1, 0, 1, 0)
            .Expand();
    }

    /// <summary>
    /// Returns the slash-commands matching the current buffer prefix.
    /// Empty result means "don't show the popup" (no slash, cursor
    /// past the command word, or no prefix match).
    /// </summary>
    internal List<(string Name, string Desc)> MenuMatches()
    {
        var text = _buffer.ToString();
        // Build a key combining the text and cursor so we can skip
        // recomputation when nothing changed since the last call.
        var key = string.Create(CultureInfo.InvariantCulture, $"{text}|{_cursor}");
        if (key == _cachedMenuKey)
            return _cachedMenuResult;

        _cachedMenuKey = key;

        if (text.Length == 0 || text[0] != '/')
        {
            _cachedMenuResult = [];
            return _cachedMenuResult;
        }

        var space = text.IndexOf(' ');
        var cursorInCommandWord = space < 0 || _cursor <= space;
        if (!cursorInCommandWord)
        {
            _cachedMenuResult = [];
            return _cachedMenuResult;
        }

        var prefix = space < 0 ? text[1..] : text[1..space];
        var session = _session;

        var available = CommandCatalog
            .Where(c => session is null || c.Available(session));

        var filtered = prefix.Length == 0
            ? available
            : available.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        _cachedMenuResult = [.. filtered.Select(c => (c.Name, c.Desc))];
        return _cachedMenuResult;
    }

    internal bool IsMenuActive() => MenuMatches().Count > 0;

    internal void AcceptCompletion(string name)
    {
        _buffer.Clear();
        _buffer.Append('/').Append(name).Append(' ');
        _cursor = _buffer.Length;
        _menuSelectedIndex = 0;
    }

    private Grid BuildMenu(List<(string Name, string Desc)> matches)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn(new GridColumn());

        for (var i = 0; i < matches.Count; i++)
        {
            var selected = i == _menuSelectedIndex;

            var indicator = selected
                ? $"[bold rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]▸[/]"
                : " ";

            var name = selected
                ? $"[bold rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]/{Markup.Escape(matches[i].Name)}[/]"
                : $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]/{Markup.Escape(matches[i].Name)}[/]";

            var desc = selected
                ? $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]{Markup.Escape(matches[i].Desc)}[/]"
                : $"[rgb({CliTheme.Divider.R},{CliTheme.Divider.G},{CliTheme.Divider.B})]{Markup.Escape(matches[i].Desc)}[/]";

            grid.AddRow(new Markup(indicator), new Markup(name), new Markup(desc));
        }

        return grid;
    }

    /// <summary>
    /// Dashed horizontal rule the width of the Panel's inner area.
    /// Spectre's built-in <see cref="Rule"/> hard-codes the '─' glyph,
    /// so we render our own with light-quadruple-dash (┄) in a dim
    /// color that sits beneath Muted.
    /// </summary>
    private static Markup BuildDashedDivider()
    {
        int innerWidth;
        try
        { innerWidth = Math.Max(8, Console.WindowWidth - 4); }
        catch (IOException) { innerWidth = 76; }

        var dashes = new string('┄', innerWidth);
        return new Markup(
            $"[rgb({CliTheme.Divider.R},{CliTheme.Divider.G},{CliTheme.Divider.B})]{dashes}[/]");
    }

    private Markup BuildInputMarkup(bool focused)
    {
        string content;
        if (_buffer.Length == 0)
        {
            var cursorGlyph = focused ? Cursor() : string.Empty;
            var placeholder = $"[rgb({CliTheme.Divider.R},{CliTheme.Divider.G},{CliTheme.Divider.B})]{Markup.Escape(Placeholder)}[/]";
            content = cursorGlyph + placeholder;
        }
        else
        {
            var text = _buffer.ToString();
            var before = text[.._cursor];
            var after = text[_cursor..];
            content = $"{Markup.Escape(before)}{Cursor()}{Markup.Escape(after)}";
        }

        return new Markup(content);
    }

    private string Cursor()
        => _cursorOn
            ? $"[bold rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]▏[/]"
            : " ";

    private static Grid BuildFooter(TuiSession session, bool menuActive, bool exitArmed)
    {
        var sep = $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]·[/]";

        // Left-side label: exit-armed state takes priority, then the
        // autocomplete hint, then the default "type / for commands".
        string glyphs;
        if (exitArmed)
        {
            glyphs =
                $"[bold rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]⚠ Ctrl+C again to exit[/]";
        }
        else if (menuActive)
        {
            glyphs =
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]↑/↓ choose[/]  " +
                $"[bold rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]Tab[/] " +
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]accept[/]";
        }
        else
        {
            glyphs =
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]type[/] " +
                $"[bold rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]/[/] " +
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]for commands[/]";
        }

        // Context chip
        string context;
        if (session.WorkspaceName is null)
        {
            context = $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]no workspace[/]";
        }
        else if (session.AgentName is null)
        {
            context =
                $"[rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]{Markup.Escape(session.WorkspaceName)}[/] " +
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]· no agent[/]";
        }
        else
        {
            context =
                $"[rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]{Markup.Escape(session.WorkspaceName)}[/]" +
                $" {sep} " +
                $"[rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]{Markup.Escape(session.AgentName)}[/]";
        }

        // Readiness badge — analogue of "Auto mode"
        string badge;
        if (session.IsRunning && session.AgentName is not null)
        {
            badge = $"[rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]⚡ Ready[/]";
        }
        else if (session.HasWorkspace && !session.IsRunning)
        {
            badge = $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]◐ Stopped[/]";
        }
        else
        {
            badge = $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]◯ Idle[/]";
        }

        var hint = $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]↵ send  ·  Shift+↵ newline  ·  Ctrl+C exit[/]";

        // Use a Grid so the left cluster hugs the left edge and the
        // right cluster hugs the right — mirroring the screenshot.
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn())
            .AddColumn(new GridColumn().NoWrap().PadLeft(2).RightAligned())
            .AddColumn(new GridColumn().NoWrap().PadLeft(2).RightAligned());

        grid.AddRow(
            new Markup(glyphs),
            new Markup(context),
            new Markup(string.Empty),
            new Markup(badge),
            new Markup(hint));

        return grid;
    }

    private static bool KeyAvailable()
    {
        try
        { return Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; }
    }
}

internal enum ComposerStatus { Submitted, Cancelled }

internal sealed record ComposerResult(ComposerStatus Status, string Text);
