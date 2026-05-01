using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from the composer.")]
internal sealed class ChatComposerRenderer
{
    public Panel Build(ChatComposerRenderModel model)
    {
        var children = new List<IRenderable>();
        if (model.Matches.Count > 0)
        {
            children.Add(BuildMenu(model.Matches, model.MenuSelectedIndex));
            children.Add(new Markup(string.Empty));
        }
        children.Add(BuildInputMarkup(model));
        children.Add(BuildDashedDivider());
        children.Add(BuildFooter(model.Session, menuActive: model.Matches.Count > 0, model.ExitArmed));

        var border = model.Focused ? CliTheme.Accent : CliTheme.Muted;

        return new Panel(new Rows(children))
            .Border(BoxBorder.Rounded)
            .BorderColor(border)
            .Padding(1, 0, 1, 0)
            .Expand();
    }

    private static Grid BuildMenu(IReadOnlyList<(string Name, string Desc)> matches, int menuSelectedIndex)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(2))
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn(new GridColumn());

        for (var index = 0; index < matches.Count; index++)
        {
            var selected = index == menuSelectedIndex;

            var indicator = selected
                ? $"[bold rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]▸[/]"
                : " ";

            var name = selected
                ? $"[bold rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]/{Markup.Escape(matches[index].Name)}[/]"
                : $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]/{Markup.Escape(matches[index].Name)}[/]";

            var desc = selected
                ? $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]{Markup.Escape(matches[index].Desc)}[/]"
                : $"[rgb({CliTheme.Divider.R},{CliTheme.Divider.G},{CliTheme.Divider.B})]{Markup.Escape(matches[index].Desc)}[/]";

            grid.AddRow(new Markup(indicator), new Markup(name), new Markup(desc));
        }

        return grid;
    }

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

    private static Markup BuildInputMarkup(ChatComposerRenderModel model)
    {
        string content;
        if (model.Text.Length == 0)
        {
            var cursorGlyph = model.Focused ? Cursor(model.CursorOn) : string.Empty;
            var placeholder = $"[rgb({CliTheme.Divider.R},{CliTheme.Divider.G},{CliTheme.Divider.B})]{Markup.Escape(model.Placeholder)}[/]";
            content = cursorGlyph + placeholder;
        }
        else
        {
            var before = model.Text[..model.Cursor];
            var after = model.Text[model.Cursor..];
            content = $"{Markup.Escape(before)}{Cursor(model.CursorOn)}{Markup.Escape(after)}";
        }

        return new Markup(content);
    }

    private static string Cursor(bool cursorOn)
        => cursorOn
            ? $"[bold rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]▏[/]"
            : " ";

    private static Grid BuildFooter(TuiSession session, bool menuActive, bool exitArmed)
    {
        var sep = $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]·[/]";

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

        string badge;
        if (session.IsRunning && session.AgentName is not null)
            badge = $"[rgb({CliTheme.Accent.R},{CliTheme.Accent.G},{CliTheme.Accent.B})]⚡ Ready[/]";
        else if (session.HasWorkspace && !session.IsRunning)
            badge = $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]◐ Stopped[/]";
        else
            badge = $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]◯ Idle[/]";

        var hint = $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]↵ send  ·  Shift+↵ newline  ·  Ctrl+C exit[/]";

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
}
