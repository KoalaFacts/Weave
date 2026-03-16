using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class CliTheme
{
    // ── Brand palette ──────────────────────────────────────────────
    public static readonly Color Primary = new(0, 188, 212);      // Teal
    public static readonly Color Accent = new(179, 136, 255);     // Soft violet
    public static readonly Color Surface = new(38, 50, 56);       // Dark slate (panel bg hint)

    // ── Semantic colors ────────────────────────────────────────────
    public static readonly Color Success = new(102, 187, 106);    // Green
    public static readonly Color Error = new(239, 83, 80);        // Coral-red
    public static readonly Color Warning = new(255, 167, 38);     // Amber
    public static readonly Color Muted = new(120, 144, 156);      // Blue-gray
    public static readonly Color Info = new(79, 195, 247);        // Light blue

    // ── Reusable styles ────────────────────────────────────────────
    public static readonly Style BrandStyle = new(Primary, decoration: Decoration.Bold);
    public static readonly Style AccentStyle = new(Accent);
    public static readonly Style SuccessStyle = new(Success);
    public static readonly Style ErrorStyle = new(Error);
    public static readonly Style WarningStyle = new(Warning);
    public static readonly Style MutedStyle = new(Muted);
    public static readonly Style ValueStyle = new(Color.White, decoration: Decoration.Bold);
    public static readonly Style PromptHighlight = new(Primary, decoration: Decoration.Bold);

    // ── Icons ──────────────────────────────────────────────────────
    public const string IconSuccess = "✔";
    public const string IconError = "✖";
    public const string IconWarning = "⚠";
    public const string IconBullet = "›";
    public const string IconBrand = "◆";

    // ── Banner ─────────────────────────────────────────────────────
    public static void WriteBanner()
    {
        AnsiConsole.Write(
            new FigletText("Weave")
                .Color(Primary));
        AnsiConsole.Write(new Rule().RuleStyle(MutedStyle));
    }

    // ── Section header (thin rule with label) ──────────────────────
    public static void WriteSection(string title)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Rule($"[bold]{Markup.Escape(title)}[/]")
                .RuleStyle(new Style(Accent))
                .LeftJustified());
    }

    // ── Semantic message helpers ───────────────────────────────────
    public static void WriteSuccess(string message)
        => AnsiConsole.MarkupLine($"[rgb({Success.R},{Success.G},{Success.B})]{IconSuccess} {Markup.Escape(message)}[/]");

    public static void WriteError(string message)
        => AnsiConsole.MarkupLine($"[rgb({Error.R},{Error.G},{Error.B})]{IconError} {Markup.Escape(message)}[/]");

    public static void WriteWarning(string message)
        => AnsiConsole.MarkupLine($"[rgb({Warning.R},{Warning.G},{Warning.B})]{IconWarning} {Markup.Escape(message)}[/]");

    public static void WriteInfo(string message)
        => AnsiConsole.MarkupLine($"[rgb({Info.R},{Info.G},{Info.B})]{IconBullet} {Markup.Escape(message)}[/]");

    public static void WriteMuted(string message)
        => AnsiConsole.MarkupLine($"[rgb({Muted.R},{Muted.G},{Muted.B})]{Markup.Escape(message)}[/]");

    public static void WriteKeyValue(string key, string value)
        => AnsiConsole.MarkupLine(
            $"  [rgb({Muted.R},{Muted.G},{Muted.B})]{Markup.Escape(key)}:[/] [bold white]{Markup.Escape(value)}[/]");

    // ── Table factory ──────────────────────────────────────────────
    public static Table CreateTable(string? title = null)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Muted);

        if (title is not null)
        {
            table.Title($"[bold rgb({Primary.R},{Primary.G},{Primary.B})]{Markup.Escape(title)}[/]");
        }

        return table;
    }

    public static TableColumn StyledColumn(string header)
        => new($"[rgb({Accent.R},{Accent.G},{Accent.B})]{Markup.Escape(header)}[/]");

    // ── Panel factory ──────────────────────────────────────────────
    public static Panel CreatePanel(string content, string header)
        => new Panel(content)
            .Header($"[bold rgb({Primary.R},{Primary.G},{Primary.B})]{Markup.Escape(header)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Muted);

    // ── Prompt styling helpers ─────────────────────────────────────
    public static SelectionPrompt<T> Styled<T>(this SelectionPrompt<T> prompt) where T : notnull
        => prompt.HighlightStyle(PromptHighlight);

    public static MultiSelectionPrompt<T> Styled<T>(this MultiSelectionPrompt<T> prompt) where T : notnull
        => prompt.HighlightStyle(PromptHighlight);

    public static TextPrompt<T> Styled<T>(this TextPrompt<T> prompt)
        => prompt.PromptStyle(AccentStyle);
}
