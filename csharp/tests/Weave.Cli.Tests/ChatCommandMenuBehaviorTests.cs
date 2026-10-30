namespace Weave.Cli.Tests;

public sealed class ChatCommandMenuBehaviorTests
{
    [Fact]
    public void MenuMatches_NoWorkspace_OffersOnlyCommandsThatCanRunWithoutWorkspace()
    {
        var editor = NewEditor(new TuiSession(new ChatComposerManifestResolver()));
        Type(editor, "/");

        var names = editor.MenuMatches().Select(match => match.Name).ToArray();

        names.ShouldBe(["help", "open", "ports", "config", "refresh", "new", "presets", "webui", "system", "version", "upgrade", "clear", "quit"]);
        editor.IsMenuActive().ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, false, "up,use,agents,status,validate")]
    [InlineData(false, true, "up,use,agents,history,status,validate")]
    [InlineData(true, false, "down,use,agents,watch,tools,status,validate")]
    [InlineData(true, true, "down,use,agents,watch,tools,tasks,history,status,validate")]
    public void MenuMatches_WorkspaceLifecycle_OffersCommandsForCurrentState(bool running, bool agentSelected, string expected)
    {
        var session = OpenWorkspace();
        if (running)
            session.MarkRunning("workspace-running");
        if (agentSelected)
            session.AgentName = "reviewer";
        var editor = NewEditor(session);
        Type(editor, "/");
        string[] contextCommands = ["up", "down", "use", "agents", "watch", "tools", "tasks", "history", "status", "validate"];

        var available = editor.MenuMatches().Select(match => match.Name).Where(contextCommands.Contains).ToArray();

        available.ShouldBe(expected.Split(','));
    }

    [Fact]
    public void BeginRead_WorkspaceStartedSincePreviousRead_RefreshesAvailableCommands()
    {
        var session = OpenWorkspace();
        var editor = NewEditor(session);
        Type(editor, "/");
        editor.MenuMatches().Select(match => match.Name).ShouldContain("up");
        editor.EndRead();
        session.MarkRunning("workspace-running");

        editor.BeginRead(session);
        Type(editor, "/");

        var names = editor.MenuMatches().Select(match => match.Name).ToArray();
        names.ShouldContain("down");
        names.ShouldNotContain("up");
    }

    [Theory]
    [InlineData("ordinary text", 13, "")]
    [InlineData("/He", 3, "help")]
    [InlineData("/open manifest.json", 5, "open")]
    [InlineData("/open manifest.json", 6, "")]
    [InlineData("/does-not-exist", 15, "")]
    public void Matches_CommandPrefixAndCursor_RestrictsCompletionToCommandWord(string text, int cursor, string expected)
    {
        var menu = new ChatCommandMenu();

        var matches = menu.Matches(text, cursor, new TuiSession(new ChatComposerManifestResolver()));

        matches.Select(match => match.Name).ToArray().ShouldBe(expected.Length == 0 ? [] : [expected]);
    }

    [Fact]
    public void HandleKey_MenuNavigation_WrapsBothDirectionsAndTabAcceptsWithoutSubmission()
    {
        var editor = NewEditor(new TuiSession(new ChatComposerManifestResolver()));
        Type(editor, "/p");
        editor.MenuMatches().Select(match => match.Name).ToArray().ShouldBe(["ports", "presets"]);

        editor.HandleKey(Key(ConsoleKey.UpArrow), out _).ShouldBeTrue();
        editor.MenuSelectedIndex.ShouldBe(1);
        editor.HandleKey(Key(ConsoleKey.DownArrow), out _).ShouldBeTrue();
        editor.MenuSelectedIndex.ShouldBe(0);
        editor.HandleKey(Key(ConsoleKey.DownArrow), out _);
        editor.HandleKey(Key(ConsoleKey.Tab), out var submitted).ShouldBeTrue();

        submitted.ShouldBeFalse();
        editor.Text.ShouldBe("/presets ");
        editor.CursorPosition.ShouldBe(9);
        editor.MenuSelectedIndex.ShouldBe(0);
        editor.IsMenuActive().ShouldBeFalse();
    }

    [Fact]
    public void HandleKey_EnterOnSelectedCommand_CompletesAndSubmitsSelection()
    {
        var editor = NewEditor(new TuiSession(new ChatComposerManifestResolver()));
        Type(editor, "/p");
        editor.HandleKey(Key(ConsoleKey.DownArrow), out _);

        editor.HandleKey(Key(ConsoleKey.Enter), out var submitted).ShouldBeTrue();

        submitted.ShouldBeTrue();
        editor.Text.ShouldBe("/presets ");
        editor.CursorPosition.ShouldBe(9);
        editor.MenuSelectedIndex.ShouldBe(0);
    }

    [Fact]
    public void NormalizeMenuSelection_PrefixNarrowsAndThenDisappears_ClampsSelectionAndClearsMenu()
    {
        var editor = NewEditor(new TuiSession(new ChatComposerManifestResolver()));
        Type(editor, "/p");
        editor.HandleKey(Key(ConsoleKey.DownArrow), out _);
        editor.MenuSelectedIndex.ShouldBe(1);
        Type(editor, "o");

        editor.NormalizeMenuSelection(editor.MenuMatches());

        editor.MenuSelectedIndex.ShouldBe(0);
        editor.MenuMatches().Select(match => match.Name).ToArray().ShouldBe(["ports"]);
        editor.HandleKey(Key(ConsoleKey.Escape), out _);
        editor.NormalizeMenuSelection(editor.MenuMatches());
        editor.MenuMatches().ShouldBeEmpty();
        editor.MenuSelectedIndex.ShouldBe(0);
        editor.Text.ShouldBeEmpty();
    }

    [Fact]
    public void Matches_CursorMovesFromCommandIntoArgument_HidesPreviouslyCachedMatches()
    {
        var menu = new ChatCommandMenu();
        var session = new TuiSession(new ChatComposerManifestResolver());
        menu.Matches("/open workspace", 5, session).Select(match => match.Name).ShouldContain("open");

        var matches = menu.Matches("/open workspace", 6, session);

        matches.ShouldBeEmpty();
    }

    private static TuiSession OpenWorkspace()
    {
        var resolver = new ChatComposerManifestResolver(Path.Join(Path.GetTempPath(), $"weave-menu-{Guid.NewGuid():N}", "workspace.json"));
        var session = new TuiSession(resolver);
        session.TryOpen("research", out var error).ShouldBeTrue();
        error.ShouldBeNull();
        return session;
    }

    private static ChatComposerEditor NewEditor(TuiSession session)
    {
        var editor = new ChatComposerEditor(TimeProvider.System);
        editor.BeginRead(session);
        return editor;
    }

    private static void Type(ChatComposerEditor editor, string text)
    {
        foreach (var character in text)
            editor.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, false, false, false), out _);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
}
