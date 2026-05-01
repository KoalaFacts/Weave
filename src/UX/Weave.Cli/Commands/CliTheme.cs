using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class CliTheme
{
    // ── NO_COLOR support (https://no-color.org) ────────────────────
    // When the NO_COLOR env var is set (any value), disable color.
    public static readonly bool NoColor =
        Environment.GetEnvironmentVariable("NO_COLOR") is not null;

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
    public static readonly Color Divider = new(70, 80, 90);       // Dim slate — subordinate to Muted

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
    // Text-presentation glyphs only. The "heavy" U+2714/U+2716/U+26A0
    // variants trigger emoji rendering in some terminal fonts, which
    // ignores our RGB color tags (the error cross turned up purple).
    public const string IconSuccess = "✓";
    public const string IconError = "✗";
    public const string IconWarning = "⚠\uFE0E"; // force text presentation
    public const string IconBullet = "›";
    public const string IconBrand = "◆";

    /// <summary>
    /// If <c>NO_COLOR</c> is set, disables Spectre.Console color output.
    /// Safe to call more than once — the profile is idempotent.
    /// </summary>
    public static void ApplyNoColor()
    {
        if (NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
    }

    // ── Banner ─────────────────────────────────────────────────────
    public static void WriteBanner()
    {
        ApplyNoColor();

        AnsiConsole.Write(
            new FigletText("Weave")
                .Color(Primary));
        AnsiConsole.Write(new Rule().RuleStyle(MutedStyle));

        var versionService = new VersionService();
        var current = versionService.Current();
        var pending = versionService.PendingUpdateFromCache();
        if (pending is not null)
        {
            AnsiConsole.MarkupLine(
                $"[rgb({Muted.R},{Muted.G},{Muted.B})]weave[/] " +
                $"[bold rgb({Primary.R},{Primary.G},{Primary.B})]v{Markup.Escape(current)}[/] " +
                $"[rgb({Warning.R},{Warning.G},{Warning.B})]↑ v{Markup.Escape(pending)} available[/] " +
                $"[rgb({Muted.R},{Muted.G},{Muted.B})]· run /upgrade for details[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[rgb({Muted.R},{Muted.G},{Muted.B})]weave[/] " +
                $"[bold rgb({Primary.R},{Primary.G},{Primary.B})]v{Markup.Escape(current)}[/]");
        }
        AnsiConsole.WriteLine();
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

    // ── REPL prompt + chat rendering ───────────────────────────────
    public static string PromptPrefix(string? workspace, string? agent)
    {
        var wsPart = workspace is null
            ? $"[rgb({Muted.R},{Muted.G},{Muted.B})](no workspace)[/]"
            : $"[rgb({Primary.R},{Primary.G},{Primary.B})]{Markup.Escape(workspace)}[/]";

        var agentPart = agent is null
            ? $"[rgb({Muted.R},{Muted.G},{Muted.B})](no agent)[/]"
            : $"[rgb({Accent.R},{Accent.G},{Accent.B})]{Markup.Escape(agent)}[/]";

        var arrow = $"[bold rgb({Primary.R},{Primary.G},{Primary.B})]›[/]";
        var sep = $"[rgb({Muted.R},{Muted.G},{Muted.B})]·[/]";
        return $"{wsPart} {sep} {agentPart} {arrow}";
    }

    public static void WriteUserEcho(string text)
        => AnsiConsole.MarkupLine(
            $"[rgb({Accent.R},{Accent.G},{Accent.B})]{IconBullet} {Markup.Escape(text)}[/]");

    public static void WriteAgentReply(string agentName, string text)
    {
        AnsiConsole.MarkupLine(
            $"[bold rgb({Primary.R},{Primary.G},{Primary.B})]{Markup.Escape(agentName)}[/] " +
            $"[rgb({Muted.R},{Muted.G},{Muted.B})]·[/] {Markup.Escape(text)}");
    }
}
