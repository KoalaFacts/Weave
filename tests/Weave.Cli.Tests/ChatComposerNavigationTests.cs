using Microsoft.Extensions.Time.Testing;

namespace Weave.Cli.Tests;

public sealed class ChatComposerNavigationTests
{
    [Fact]
    public void HandleKey_HistoryExceedsCapacity_RecallsNewestFiftyAndStopsAtBothBounds()
    {
        var editor = new ChatComposerEditor(new FakeTimeProvider());
        for (var index = 0; index < 51; index++)
            editor.RecordSubmittedText($"message-{index}");
        editor.RecordSubmittedText(string.Empty);
        editor.BeginRead(NewSession());

        for (var index = 50; index >= 1; index--)
        {
            editor.HandleKey(Key(ConsoleKey.UpArrow), out var submitted).ShouldBeTrue();
            submitted.ShouldBeFalse();
            editor.Text.ShouldBe($"message-{index}");
            editor.CursorPosition.ShouldBe(editor.Text.Length);
        }
        editor.HandleKey(Key(ConsoleKey.UpArrow), out _).ShouldBeFalse();
        editor.Text.ShouldBe("message-1");
        for (var index = 2; index <= 50; index++)
        {
            editor.HandleKey(Key(ConsoleKey.DownArrow), out _).ShouldBeTrue();
            editor.Text.ShouldBe($"message-{index}");
        }
        editor.HandleKey(Key(ConsoleKey.DownArrow), out _).ShouldBeTrue();
        editor.Text.ShouldBeEmpty();
        editor.CursorPosition.ShouldBe(0);
        editor.HandleKey(Key(ConsoleKey.DownArrow), out _).ShouldBeFalse();
        editor.Text.ShouldBeEmpty();
    }

    [Fact]
    public void BeginRead_PreviousReadHadHistoryAndExitRequest_ResetsDraftExitAndHistoryPosition()
    {
        var editor = new ChatComposerEditor(new FakeTimeProvider());
        editor.RecordSubmittedText("first request");
        editor.RecordSubmittedText("second request");
        editor.BeginRead(NewSession());
        editor.HandleKey(Key(ConsoleKey.UpArrow), out _);
        editor.HandleKey(Key(ConsoleKey.UpArrow), out _);
        editor.HandleKey(Key(ConsoleKey.C, control: true), out _);
        editor.HandleKey(Key(ConsoleKey.C, control: true), out _);
        editor.ExitRequested.ShouldBeTrue();
        editor.EndRead();

        editor.BeginRead(NewSession());

        editor.Text.ShouldBeEmpty();
        editor.CursorPosition.ShouldBe(0);
        editor.ExitRequested.ShouldBeFalse();
        editor.IsExitArmed.ShouldBeFalse();
        editor.HandleKey(Key(ConsoleKey.UpArrow), out _).ShouldBeTrue();
        editor.Text.ShouldBe("second request");
    }

    [Theory]
    [InlineData(ConsoleKey.UpArrow)]
    [InlineData(ConsoleKey.DownArrow)]
    public void HandleKey_EmptyHistory_PreservesSingleLineDraft(ConsoleKey key)
    {
        var editor = new ChatComposerEditor(TimeProvider.System);
        editor.BeginRead(NewSession());
        Type(editor, "keep this draft");

        var changed = editor.HandleKey(Key(key), out var submitted);

        changed.ShouldBeFalse();
        submitted.ShouldBeFalse();
        editor.Text.ShouldBe("keep this draft");
        editor.CursorPosition.ShouldBe(15);
    }

    [Fact]
    public void HandleKey_MultilineArrows_ClampColumnAndDoNotRecallHistory()
    {
        var editor = new ChatComposerEditor(TimeProvider.System);
        editor.RecordSubmittedText("unrelated history");
        editor.BeginRead(NewSession());
        Type(editor, "abcdef");
        editor.HandleKey(Key(ConsoleKey.Enter, shift: true), out _);
        Type(editor, "xy");
        editor.HandleKey(Key(ConsoleKey.Enter, shift: true), out _);
        Type(editor, "12345");

        editor.HandleKey(Key(ConsoleKey.UpArrow), out _).ShouldBeTrue();
        editor.CursorPosition.ShouldBe(9);
        editor.HandleKey(Key(ConsoleKey.UpArrow), out _).ShouldBeTrue();
        editor.CursorPosition.ShouldBe(2);
        editor.HandleKey(Key(ConsoleKey.UpArrow), out _).ShouldBeFalse();
        editor.HandleKey(Key(ConsoleKey.DownArrow), out _).ShouldBeTrue();
        editor.CursorPosition.ShouldBe(9);
        editor.HandleKey(Key(ConsoleKey.DownArrow), out _).ShouldBeTrue();
        editor.CursorPosition.ShouldBe(12);
        editor.HandleKey(Key(ConsoleKey.DownArrow), out _).ShouldBeFalse();
        editor.Text.ShouldBe("abcdef\nxy\n12345");
    }

    [Fact]
    public void HandleKey_ControlUOnSecondLine_RemovesOnlyTextBeforeCursorOnThatLine()
    {
        var editor = new ChatComposerEditor(TimeProvider.System);
        Type(editor, "keep first");
        editor.HandleKey(Key(ConsoleKey.Enter, shift: true), out _);
        Type(editor, "remove suffix");
        for (var index = 0; index < 6; index++)
            editor.HandleKey(Key(ConsoleKey.LeftArrow), out _);

        editor.HandleKey(Key(ConsoleKey.U, control: true), out var submitted).ShouldBeTrue();

        submitted.ShouldBeFalse();
        editor.Text.ShouldBe("keep first\nsuffix");
        editor.CursorPosition.ShouldBe(11);
    }

    [Fact]
    public void HandleKey_ControlWAfterWhitespace_RemovesWhitespaceAndPreviousWord()
    {
        var editor = new ChatComposerEditor(TimeProvider.System);
        Type(editor, "keep remove   suffix");
        for (var index = 0; index < 6; index++)
            editor.HandleKey(Key(ConsoleKey.LeftArrow), out _);

        editor.HandleKey(Key(ConsoleKey.W, control: true), out _).ShouldBeTrue();

        editor.Text.ShouldBe("keep suffix");
        editor.CursorPosition.ShouldBe(5);
    }

    [Fact]
    public void HandleKey_AltEnter_InsertsNewlineAtCursorWithoutSubmitting()
    {
        var editor = new ChatComposerEditor(TimeProvider.System);
        Type(editor, "ab");
        editor.HandleKey(Key(ConsoleKey.LeftArrow), out _);

        editor.HandleKey(Key(ConsoleKey.Enter, alt: true), out var submitted).ShouldBeTrue();

        submitted.ShouldBeFalse();
        editor.Text.ShouldBe("a\nb");
        editor.CursorPosition.ShouldBe(2);
    }

    [Theory]
    [InlineData(ConsoleKey.Tab)]
    [InlineData(ConsoleKey.Escape)]
    [InlineData(ConsoleKey.F1)]
    public void HandleKey_NoApplicableAction_DoesNotChangeEmptyInput(ConsoleKey key)
    {
        var editor = new ChatComposerEditor(TimeProvider.System);

        editor.HandleKey(Key(key), out var submitted).ShouldBeFalse();

        submitted.ShouldBeFalse();
        editor.Text.ShouldBeEmpty();
        editor.CursorPosition.ShouldBe(0);
    }

    [Fact]
    public void HandleKey_ExitConfirmationAtDeadline_RearmsInsteadOfExiting()
    {
        var clock = new FakeTimeProvider();
        var editor = new ChatComposerEditor(clock);
        editor.HandleKey(Key(ConsoleKey.C, control: true), out _);
        editor.IsExitArmed.ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(2));
        editor.IsExitArmed.ShouldBeFalse();
        editor.HasExpiredExitHint.ShouldBeTrue();

        editor.HandleKey(Key(ConsoleKey.C, control: true), out var submitted);

        submitted.ShouldBeFalse();
        editor.ExitRequested.ShouldBeFalse();
        editor.IsExitArmed.ShouldBeTrue();
        editor.HasExpiredExitHint.ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        editor.HandleKey(Key(ConsoleKey.C, control: true), out _);
        editor.ExitRequested.ShouldBeTrue();
    }

    [Fact]
    public void HandleKey_OrdinaryKeyBetweenControlCPresses_CancelsExitConfirmation()
    {
        var editor = new ChatComposerEditor(new FakeTimeProvider());
        editor.HandleKey(Key(ConsoleKey.C, control: true), out _);
        Type(editor, "x");
        editor.IsExitArmed.ShouldBeFalse();

        editor.HandleKey(Key(ConsoleKey.C, control: true), out _);

        editor.ExitRequested.ShouldBeFalse();
        editor.IsExitArmed.ShouldBeTrue();
        editor.Text.ShouldBe("x");
    }

    [Fact]
    public void ExpireExitHint_ExpiredHint_ClearsHintWithoutRequestingExit()
    {
        var clock = new FakeTimeProvider();
        var editor = new ChatComposerEditor(clock);
        editor.HandleKey(Key(ConsoleKey.C, control: true), out _);
        clock.Advance(TimeSpan.FromSeconds(2));
        editor.HasExpiredExitHint.ShouldBeTrue();

        editor.ExpireExitHint();

        editor.HasExpiredExitHint.ShouldBeFalse();
        editor.IsExitArmed.ShouldBeFalse();
        editor.ExitRequested.ShouldBeFalse();
    }

    private static TuiSession NewSession() => new(new ChatComposerManifestResolver());

    private static void Type(ChatComposerEditor editor, string text)
    {
        foreach (var character in text)
            editor.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, false, false, false), out _);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, bool control = false, bool shift = false, bool alt = false) =>
        new('\0', key, shift, alt, control);
}
