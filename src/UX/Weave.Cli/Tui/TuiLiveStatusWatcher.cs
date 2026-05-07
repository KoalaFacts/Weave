using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Actions.Context;
using Weave.Actions.Workspace;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

internal sealed class TuiLiveStatusWatcher
{
    private readonly WatchWorkspaceAction _watchAction;

    public TuiLiveStatusWatcher(WatchWorkspaceAction watchAction)
    {
        _watchAction = watchAction;
    }

    public async Task WatchAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var workspaceId = TuiWorkspaceStateReader.ReadWorkspaceId(manifestPath);
        if (workspaceId is null)
        {
            CliTheme.WriteWarning("No workspace ID on disk — nothing to watch.");
            return;
        }

        RenderWatchHeader(manifest.Name);

        var placeholder = new Markup(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]Loading…[/]");

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await AnsiConsole.Live(placeholder)
                .AutoClear(false)
                .StartAsync(async context => await WatchLoopAsync(context, workspaceId, manifestPath, manifest, lifetime));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WatchLoopAsync(
        LiveDisplayContext context,
        string workspaceId,
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationTokenSource lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            context.UpdateTarget(await BuildWatchFrameAsync(workspaceId, manifestPath, manifest, lifetime.Token));

            for (var index = 0; index < 20 && !lifetime.IsCancellationRequested; index++)
            {
                if (KeyPressed())
                {
                    DrainKey();
                    await lifetime.CancelAsync();
                    break;
                }

                await Task.Delay(100, lifetime.Token);
            }
        }
    }

    private async Task<IRenderable> BuildWatchFrameAsync(
        string workspaceId,
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var watch = await _watchAction.ExecuteAsync(new WatchWorkspaceInput(workspaceId), cancellationToken);
        if (!watch.IsSuccess)
        {
            if (watch.Failure.Reason == ActionFailureReason.Cancelled)
                throw new OperationCanceledException(cancellationToken);

            var color = watch.Failure.Reason == ActionFailureReason.SiloUnreachable
                ? "Silo is not reachable. Retrying…"
                : $"Live status error: {Markup.Escape(watch.Failure.Message)}";
            return new Markup(
                $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]{color}[/]");
        }

        return TuiLiveStatusRenderer.Build(
            manifestPath, manifest, watch.Value.Workspace, watch.Value.Agents, watch.Value.Tools);
    }

    private static void RenderWatchHeader(string workspaceName)
    {
        AnsiConsole.Clear();
        var rule = new Rule(
            $"[bold rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]◆ Weave[/] " +
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]· TUI[/]")
            .RuleStyle(CliTheme.MutedStyle)
            .LeftJustified();
        AnsiConsole.Write(rule);
        CliTheme.WriteSection($"Watching · {workspaceName}");
        AnsiConsole.MarkupLine(
            $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]" +
            "Auto-refresh every 2s · press any key to return[/]");
        AnsiConsole.WriteLine();
    }

    private static bool KeyPressed()
    {
        try
        {
            return Console.KeyAvailable;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void DrainKey()
    {
        try
        {
            while (Console.KeyAvailable)
                _ = Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
