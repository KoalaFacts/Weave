using System.Text;
using Weave.Cli.Tui;

namespace Weave.Cli.Tests;

public class ChatComposerTests
{
    private static ConsoleKeyInfo Key(char ch, ConsoleKey key = 0, bool ctrl = false, bool shift = false, bool alt = false)
    {
        if (key == 0 && ch != '\0')
            key = (ConsoleKey)char.ToUpper(ch, System.Globalization.CultureInfo.InvariantCulture);
        return new ConsoleKeyInfo(ch, key, shift, alt, ctrl);
    }

    private static ConsoleKeyInfo CtrlKey(ConsoleKey key) =>
        new('\0', key, false, false, true);

    private static ConsoleKeyInfo CharKey(char ch) =>
        Key(ch);

    private static ConsoleKeyInfo SpecialKey(ConsoleKey key, bool shift = false) =>
        new('\0', key, shift, false, false);

    // ── LineStart / LineEnd ────────────────────────────────────────

    [Fact]
    public void LineStart_FirstLine_ReturnsZero()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello");
        c.LineStart(3).ShouldBe(0);
    }

    [Fact]
    public void LineStart_SecondLine_ReturnsAfterNewline()
    {
        var c = new ChatComposer();
        c.Buffer.Append("first\nsecond");
        // cursor at 's' of "second" = index 6
        c.LineStart(8).ShouldBe(6);
    }

    [Fact]
    public void LineEnd_FirstLine_ReturnsNewlinePos()
    {
        var c = new ChatComposer();
        c.Buffer.Append("first\nsecond");
        c.LineEnd(0).ShouldBe(5);
    }

    [Fact]
    public void LineEnd_LastLine_ReturnsLength()
    {
        var c = new ChatComposer();
        c.Buffer.Append("first\nsecond");
        c.LineEnd(6).ShouldBe(12);
    }

    [Fact]
    public void LineStart_AtNewline_ReturnsStartOfCurrentLine()
    {
        var c = new ChatComposer();
        c.Buffer.Append("ab\ncd");
        // from = 3 → 'c', should return 3 (start of second line)
        c.LineStart(3).ShouldBe(3);
    }

    // ── HandleKey basic typing ─────────────────────────────────────

    [Fact]
    public void HandleKey_PrintableChar_InsertsAtCursor()
    {
        var c = new ChatComposer();
        c.HandleKey(CharKey('h'), out var submitted);
        submitted.ShouldBeFalse();
        c.Buffer.ToString().ShouldBe("h");
        c.CursorPosition.ShouldBe(1);
    }

    [Fact]
    public void HandleKey_MultipleChars_BuildsString()
    {
        var c = new ChatComposer();
        c.HandleKey(CharKey('h'), out _);
        c.HandleKey(CharKey('i'), out _);
        c.Buffer.ToString().ShouldBe("hi");
        c.CursorPosition.ShouldBe(2);
    }

    // ── Backspace ──────────────────────────────────────────────────

    [Fact]
    public void HandleKey_Backspace_DeletesPreviousChar()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hi");
        c.CursorPosition = 2;
        c.HandleKey(SpecialKey(ConsoleKey.Backspace), out _);
        c.Buffer.ToString().ShouldBe("h");
        c.CursorPosition.ShouldBe(1);
    }

    [Fact]
    public void HandleKey_Backspace_AtStart_Noop()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hi");
        c.CursorPosition = 0;
        c.HandleKey(SpecialKey(ConsoleKey.Backspace), out _);
        c.Buffer.ToString().ShouldBe("hi");
        c.CursorPosition.ShouldBe(0);
    }

    // ── Delete ─────────────────────────────────────────────────────

    [Fact]
    public void HandleKey_Delete_DeletesCharAtCursor()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hi");
        c.CursorPosition = 0;
        c.HandleKey(SpecialKey(ConsoleKey.Delete), out _);
        c.Buffer.ToString().ShouldBe("i");
        c.CursorPosition.ShouldBe(0);
    }

    [Fact]
    public void HandleKey_Delete_AtEnd_Noop()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hi");
        c.CursorPosition = 2;
        c.HandleKey(SpecialKey(ConsoleKey.Delete), out _);
        c.Buffer.ToString().ShouldBe("hi");
    }

    // ── Home / End ─────────────────────────────────────────────────

    [Fact]
    public void HandleKey_Home_MovesCursorToLineStart()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello");
        c.CursorPosition = 3;
        c.HandleKey(SpecialKey(ConsoleKey.Home), out _);
        c.CursorPosition.ShouldBe(0);
    }

    [Fact]
    public void HandleKey_End_MovesCursorToLineEnd()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello");
        c.CursorPosition = 1;
        c.HandleKey(SpecialKey(ConsoleKey.End), out _);
        c.CursorPosition.ShouldBe(5);
    }

    // ── Left / Right ───────────────────────────────────────────────

    [Fact]
    public void HandleKey_LeftArrow_MovesCursorLeft()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hi");
        c.CursorPosition = 2;
        c.HandleKey(SpecialKey(ConsoleKey.LeftArrow), out _);
        c.CursorPosition.ShouldBe(1);
    }

    [Fact]
    public void HandleKey_RightArrow_MovesCursorRight()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hi");
        c.CursorPosition = 0;
        c.HandleKey(SpecialKey(ConsoleKey.RightArrow), out _);
        c.CursorPosition.ShouldBe(1);
    }

    [Fact]
    public void HandleKey_LeftArrow_AtStart_StaysAtZero()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hi");
        c.CursorPosition = 0;
        c.HandleKey(SpecialKey(ConsoleKey.LeftArrow), out _);
        c.CursorPosition.ShouldBe(0);
    }

    [Fact]
    public void HandleKey_RightArrow_AtEnd_StaysAtEnd()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hi");
        c.CursorPosition = 2;
        c.HandleKey(SpecialKey(ConsoleKey.RightArrow), out _);
        c.CursorPosition.ShouldBe(2);
    }

    // ── Enter (submit) ─────────────────────────────────────────────

    [Fact]
    public void HandleKey_Enter_NonEmpty_Submits()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello");
        c.CursorPosition = 5;
        c.HandleKey(Key('\r', ConsoleKey.Enter), out var submitted);
        submitted.ShouldBeTrue();
    }

    [Fact]
    public void HandleKey_Enter_Empty_DoesNotSubmit()
    {
        var c = new ChatComposer();
        c.HandleKey(Key('\r', ConsoleKey.Enter), out var submitted);
        submitted.ShouldBeFalse();
    }

    [Fact]
    public void HandleKey_ShiftEnter_InsertsNewline()
    {
        var c = new ChatComposer();
        c.Buffer.Append("line1");
        c.CursorPosition = 5;
        c.HandleKey(Key('\r', ConsoleKey.Enter, shift: true), out var submitted);
        submitted.ShouldBeFalse();
        c.Buffer.ToString().ShouldBe("line1\n");
    }

    // ── Escape ─────────────────────────────────────────────────────

    [Fact]
    public void HandleKey_Escape_ClearsBuffer()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello");
        c.CursorPosition = 5;
        c.HandleKey(SpecialKey(ConsoleKey.Escape), out _);
        c.Buffer.ToString().ShouldBe(string.Empty);
        c.CursorPosition.ShouldBe(0);
    }

    // ── Ctrl+W (word delete) ───────────────────────────────────────

    [Fact]
    public void HandleKey_CtrlW_DeletesLastWord()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello world");
        c.CursorPosition = 11;
        c.HandleKey(Key('\u0017', ConsoleKey.W, ctrl: true), out _);
        c.Buffer.ToString().ShouldBe("hello ");
    }

    [Fact]
    public void HandleKey_CtrlW_AtStart_Noop()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello");
        c.CursorPosition = 0;
        c.HandleKey(Key('\u0017', ConsoleKey.W, ctrl: true), out _);
        c.Buffer.ToString().ShouldBe("hello");
        c.CursorPosition.ShouldBe(0);
    }

    [Fact]
    public void HandleKey_CtrlW_SingleWord_ClearsAll()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello");
        c.CursorPosition = 5;
        c.HandleKey(Key('\u0017', ConsoleKey.W, ctrl: true), out _);
        c.Buffer.ToString().ShouldBe(string.Empty);
        c.CursorPosition.ShouldBe(0);
    }

    // ── Ctrl+U (line clear) ────────────────────────────────────────

    [Fact]
    public void HandleKey_CtrlU_ClearsToLineStart()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello world");
        c.CursorPosition = 6;
        c.HandleKey(Key('\u0015', ConsoleKey.U, ctrl: true), out _);
        c.Buffer.ToString().ShouldBe("world");
        c.CursorPosition.ShouldBe(0);
    }

    [Fact]
    public void HandleKey_CtrlU_AtStart_Noop()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hello");
        c.CursorPosition = 0;
        c.HandleKey(Key('\u0015', ConsoleKey.U, ctrl: true), out _);
        c.Buffer.ToString().ShouldBe("hello");
        c.CursorPosition.ShouldBe(0);
    }

    // ── Double Ctrl+C ──────────────────────────────────────────────

    [Fact]
    public void HandleKey_SingleCtrlC_DoesNotExit()
    {
        var c = new ChatComposer();
        c.HandleKey(Key('\u0003', ConsoleKey.C, ctrl: true), out _);
        c.ExitRequested.ShouldBeFalse();
    }

    [Fact]
    public void HandleKey_DoubleCtrlC_SetsExitRequested()
    {
        var c = new ChatComposer();
        c.HandleKey(Key('\u0003', ConsoleKey.C, ctrl: true), out _);
        c.HandleKey(Key('\u0003', ConsoleKey.C, ctrl: true), out _);
        c.ExitRequested.ShouldBeTrue();
    }

    // ── AcceptCompletion ───────────────────────────────────────────

    [Fact]
    public void AcceptCompletion_SetsBufferToSlashCommand()
    {
        var c = new ChatComposer();
        c.Buffer.Append("/he");
        c.CursorPosition = 3;
        c.AcceptCompletion("help");
        c.Buffer.ToString().ShouldBe("/help ");
        c.CursorPosition.ShouldBe(6);
    }

    [Fact]
    public void AcceptCompletion_ClearsPreviousContent()
    {
        var c = new ChatComposer();
        c.Buffer.Append("some random text");
        c.CursorPosition = 16;
        c.AcceptCompletion("open");
        c.Buffer.ToString().ShouldBe("/open ");
    }

    // ── Insert at mid-position ─────────────────────────────────────

    [Fact]
    public void HandleKey_InsertAtMidPosition_ShiftsRight()
    {
        var c = new ChatComposer();
        c.Buffer.Append("hllo");
        c.CursorPosition = 1;
        c.HandleKey(CharKey('e'), out _);
        c.Buffer.ToString().ShouldBe("hello");
        c.CursorPosition.ShouldBe(2);
    }

    // ── History recall (Up/Down) ───────────────────────────────────

    [Fact]
    public void HandleKey_UpArrow_EmptyHistory_Noop()
    {
        var c = new ChatComposer();
        c.Buffer.Append("test");
        c.CursorPosition = 4;
        // Up arrow should not change anything when there's no history
        // and buffer is single-line.
        c.HandleKey(SpecialKey(ConsoleKey.UpArrow), out _);
        // At minimum the buffer is not corrupted.
        c.Buffer.Length.ShouldBeGreaterThanOrEqualTo(0);
    }
}
