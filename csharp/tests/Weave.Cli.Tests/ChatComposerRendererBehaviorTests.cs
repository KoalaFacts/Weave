using System.Globalization;
using Spectre.Console;

namespace Weave.Cli.Tests;

public sealed class ChatComposerRendererBehaviorTests
{
    [Fact]
    public void Build_EmptyUnfocusedInput_ShowsLiteralPlaceholderAndIdleContextWithoutCursor()
    {
        var model = Model() with { Placeholder = "[red]send a request[/]", Focused = false };

        var output = Render(model);

        output.ShouldContain("[red]send a request[/]");
        output.ShouldContain("no workspace");
        output.ShouldContain("Idle");
        output.ShouldContain("for commands");
        output.ShouldNotContain("▏");
    }

    [Fact]
    public void Build_EmptyFocusedInput_ShowsCursorBeforeLiteralPlaceholder()
    {
        var model = Model() with { Placeholder = "[draft]", Focused = true, CursorOn = true };

        var output = Render(model);

        output.ShouldContain("▏[draft]");
        output.ShouldContain("Shift+↵ newline");
    }

    [Theory]
    [InlineData(true, "[red]a▏b[/]")]
    [InlineData(false, "[red]a b[/]")]
    public void Build_NonemptyInput_InsertsCursorAtPositionAndPreservesLiteralMarkup(bool cursorOn, string expected)
    {
        var model = Model() with { Text = "[red]ab[/]", Cursor = 6, CursorOn = cursorOn };

        var output = Render(model);

        output.ShouldContain(expected);
        output.ShouldNotContain("Send a message");
    }

    [Fact]
    public void Build_StoppedWorkspaceWithoutAgent_ShowsLiteralWorkspaceAndStoppedBadge()
    {
        var session = OpenWorkspace("[green]research[/]");

        var output = Render(Model(session));

        output.ShouldContain("[green]research[/]");
        output.ShouldContain("no agent");
        output.ShouldContain("Stopped");
        output.ShouldNotContain("Ready");
    }

    [Fact]
    public void Build_RunningWorkspaceWithAgent_ShowsLiteralAgentAndReadyBadge()
    {
        var session = OpenWorkspace("research");
        session.MarkRunning("running-workspace");
        session.AgentName = "[blue]reviewer[/]";

        var output = Render(Model(session));

        output.ShouldContain("research");
        output.ShouldContain("[blue]reviewer[/]");
        output.ShouldContain("Ready");
        output.ShouldNotContain("no agent");
        output.ShouldNotContain("Stopped");
    }

    [Fact]
    public void Build_RunningWorkspaceWithoutAgent_ShowsIdleBadge()
    {
        var session = OpenWorkspace("research");
        session.MarkRunning("running-workspace");

        var output = Render(Model(session));

        output.ShouldContain("no agent");
        output.ShouldContain("Idle");
        output.ShouldNotContain("Ready");
        output.ShouldNotContain("Stopped");
    }

    [Fact]
    public void Build_CommandMenu_RendersLiteralNamesDescriptionsAndSelectedIndicator()
    {
        var model = Model() with
        {
            Matches = [("[red]first[/]", "first [description]"), ("second", "second [description]")],
            MenuSelectedIndex = 1
        };

        var output = Render(model);

        output.ShouldContain("/[red]first[/]");
        output.ShouldContain("first [description]");
        output.ShouldContain("second [description]");
        var selectedLine = output.Split('\n').Single(line => line.Contains('▸'));
        selectedLine.ShouldContain("/second");
        selectedLine.ShouldNotContain("/first");
        output.ShouldContain("↑/↓ choose");
        output.ShouldContain("Tab");
        output.ShouldContain("accept");
        output.ShouldNotContain("for commands");
    }

    [Fact]
    public void Build_ExitArmedWithMenu_ShowsExitWarningInsteadOfCompletionHint()
    {
        var model = Model() with { ExitArmed = true, Matches = [("help", "Show help")] };

        var output = Render(model);

        output.ShouldContain("Ctrl+C again to exit");
        output.ShouldContain("/help");
        output.ShouldNotContain("↑/↓ choose");
        output.ShouldNotContain("for commands");
    }

    private static ChatComposerRenderModel Model(TuiSession? session = null) => new(
        session ?? new TuiSession(new ChatComposerManifestResolver()),
        string.Empty, 0, true, "Send a message", true, false, [], 0);

    private static TuiSession OpenWorkspace(string name)
    {
        var resolver = new ChatComposerManifestResolver(Path.Join(Path.GetTempPath(), $"weave-render-{Guid.NewGuid():N}", "workspace.json"));
        var session = new TuiSession(resolver);
        session.TryOpen(name, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        return session;
    }

    private static string Render(ChatComposerRenderModel model)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });
        console.Profile.Capabilities.Ansi = false;
        console.Profile.Width = 240;
        console.Write(ChatComposerRenderer.Build(model));
        return writer.ToString();
    }
}
