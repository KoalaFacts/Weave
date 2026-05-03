namespace Weave.Cli.Tui;

internal sealed record ChatComposerRenderModel(
    TuiSession Session,
    string Text,
    int Cursor,
    bool CursorOn,
    string Placeholder,
    bool Focused,
    bool ExitArmed,
    IReadOnlyList<(string Name, string Desc)> Matches,
    int MenuSelectedIndex);
