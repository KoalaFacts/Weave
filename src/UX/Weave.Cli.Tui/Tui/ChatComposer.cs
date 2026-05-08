using Spectre.Console;

namespace Weave.Cli.Tui;

/// <summary>
/// Multi-line chat composer rendered inside a rounded panel, with a
/// context-aware footer (workspace · agent · mode · Ctrl+C hint).
/// </summary>
/// <remarks>
/// Input is hand-rolled on <see cref="Console.ReadKey(bool)"/> rather
/// than <c>Spectre.Console.TextPrompt</c> so we can: show a custom
/// cursor, repaint the panel on every keystroke, and support
/// Shift+Enter for newlines.
/// </remarks>
internal sealed class ChatComposer(TimeProvider timeProvider)
{
    private readonly ChatComposerEditor _editor = new(timeProvider);
    private bool _cursorOn = true;

    // Poll loop ticks every 25 ms; 20 ticks ≈ 500 ms → classic
    // terminal blink rate. Counter resets on each keystroke so the
    // cursor stays solid while the user is actively typing.
    private const int BlinkTicks = 20;
    private int _blinkCounter;

    public string Placeholder { get; set; } = "Send a message, or / for commands…";

    public async Task<ComposerResult> ReadAsync(TuiSession session, CancellationToken ct)
    {
        var status = ComposerStatus.Cancelled;
        var cursorRestore = TryHideTerminalCursor();
        var ctrlCRestore = TryCaptureCtrlC();
        _editor.BeginRead(session);

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
                            if (_editor.HasExpiredExitHint)
                            {
                                _editor.ExpireExitHint();
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

                        if (_editor.HandleKey(key, out var submitted))
                        {
                            if (_editor.ExitRequested)
                            {
                                status = ComposerStatus.Cancelled;
                                return;
                            }

                            if (submitted)
                            {
                                var text = _editor.Text.Trim();
                                _editor.RecordSubmittedText(text);
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
            _editor.EndRead();
        }

        return new ComposerResult(status, _editor.Text);
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
        catch (IOException) { /* console redirected — ignore */ }
        catch (PlatformNotSupportedException) { /* unsupported terminal — ignore */ }

        try
        { Console.TreatControlCAsInput = true; }
        catch (IOException) { /* console redirected — ignore */ }
        catch (PlatformNotSupportedException) { /* unsupported terminal — ignore */ }

        return () =>
        {
            try
            { Console.TreatControlCAsInput = previous; }
            catch (IOException) { /* console redirected — ignore */ }
            catch (PlatformNotSupportedException) { /* unsupported terminal — ignore */ }
        };
    }

    /// <summary>
    /// Hides the terminal's blinking text cursor for the composer session
    /// and returns a restore action safe to call in a <c>finally</c>.
    /// </summary>
    /// <remarks>
    /// The getter for <see cref="Console.CursorVisible"/> is Windows-only,
    /// so we don't read the previous state — default back to visible on exit.
    /// </remarks>
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

    private Panel BuildRenderable(TuiSession session, bool focused)
    {
        var matches = _editor.MenuMatches();
        _editor.NormalizeMenuSelection(matches);

        return ChatComposerRenderer.Build(new ChatComposerRenderModel(
            session,
            _editor.Text,
            _editor.CursorPosition,
            _cursorOn,
            Placeholder,
            focused,
            _editor.IsExitArmed,
            matches,
            _editor.MenuSelectedIndex));
    }

    private static bool KeyAvailable()
    {
        try
        { return Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; }
    }
}
