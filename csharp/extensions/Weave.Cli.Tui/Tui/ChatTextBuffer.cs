using System.Text;

namespace Weave.Cli.Tui;

internal sealed class ChatTextBuffer
{
    private readonly StringBuilder _content = new();

    internal StringBuilder Buffer => _content;
    internal int CursorPosition { get; set; }
    internal int Length => _content.Length;
    internal string Text => _content.ToString();

    internal void Clear()
    {
        _content.Clear();
        CursorPosition = 0;
    }

    internal void Replace(string value)
    {
        _content.Clear();
        _content.Append(value);
        CursorPosition = _content.Length;
    }

    internal bool ContainsNewLine() => _content.ToString().Contains('\n');

    internal bool Insert(char value)
    {
        _content.Insert(CursorPosition, value);
        CursorPosition++;
        return true;
    }

    internal bool Backspace()
    {
        if (CursorPosition == 0)
            return false;

        _content.Remove(CursorPosition - 1, 1);
        CursorPosition--;
        return true;
    }

    internal bool Delete()
    {
        if (CursorPosition >= _content.Length)
            return false;

        _content.Remove(CursorPosition, 1);
        return true;
    }

    internal bool MoveLeft()
    {
        if (CursorPosition == 0)
            return false;

        CursorPosition--;
        return true;
    }

    internal bool MoveRight()
    {
        if (CursorPosition >= _content.Length)
            return false;

        CursorPosition++;
        return true;
    }

    internal bool MoveToLineStart()
    {
        CursorPosition = LineStart(CursorPosition);
        return true;
    }

    internal bool MoveToLineEnd()
    {
        CursorPosition = LineEnd(CursorPosition);
        return true;
    }

    internal bool MoveLine(bool up)
    {
        var start = LineStart(CursorPosition);
        var column = CursorPosition - start;

        if (up)
        {
            if (start == 0)
                return false;

            var prevEnd = start - 1;
            var prevStart = LineStart(prevEnd);
            var prevLen = prevEnd - prevStart;
            CursorPosition = prevStart + Math.Min(column, prevLen);
            return true;
        }

        var end = LineEnd(CursorPosition);
        if (end >= _content.Length)
            return false;

        var nextStart = end + 1;
        var nextEnd = LineEnd(nextStart);
        var nextLen = nextEnd - nextStart;
        CursorPosition = nextStart + Math.Min(column, nextLen);
        return true;
    }

    internal bool DeleteWordBeforeCursor()
    {
        if (CursorPosition == 0)
            return false;

        var s = _content.ToString();
        var end = CursorPosition;
        while (CursorPosition > 0 && char.IsWhiteSpace(s[CursorPosition - 1]))
            CursorPosition--;
        while (CursorPosition > 0 && !char.IsWhiteSpace(s[CursorPosition - 1]))
            CursorPosition--;
        _content.Remove(CursorPosition, end - CursorPosition);
        return true;
    }

    internal bool ClearToLineStart()
    {
        if (CursorPosition == 0)
            return false;

        var start = LineStart(CursorPosition);
        _content.Remove(start, CursorPosition - start);
        CursorPosition = start;
        return true;
    }

    internal int LineStart(int from)
    {
        var s = _content.ToString();
        var i = from - 1;
        while (i >= 0 && s[i] != '\n')
            i--;
        return i + 1;
    }

    internal int LineEnd(int from)
    {
        var s = _content.ToString();
        var i = from;
        while (i < s.Length && s[i] != '\n')
            i++;
        return i;
    }
}
