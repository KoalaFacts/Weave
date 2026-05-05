using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
namespace Weave.Cli.Tui;

internal static class TuiLiveStatusWatcher
{
    public static async Task WatchAsync(
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

        using var client = new WorkspaceApiClient();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await AnsiConsole.Live(placeholder)
                .AutoClear(false)
                .StartAsync(async context => await WatchLoopAsync(context, client, workspaceId, manifestPath, manifest, lifetime));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WatchLoopAsync(
        LiveDisplayContext context,
        WorkspaceApiClient client,
        string workspaceId,
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationTokenSource lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            context.UpdateTarget(await BuildWatchFrameAsync(client, workspaceId, manifestPath, manifest, lifetime.Token));

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

    private static async Task<IRenderable> BuildWatchFrameAsync(
        WorkspaceApiClient client,
        string workspaceId,
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await client.IsReachableAsync(cancellationToken))
            {
                return new Markup(
                    $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                    "Silo is not reachable. Retrying…[/]");
            }

            var workspace = await client.GetWorkspaceAsync(workspaceId, cancellationToken);
            var agents = await client.GetAgentsAsync(workspaceId, cancellationToken);
            var tools = await client.GetToolsAsync(workspaceId, cancellationToken);
            return TuiLiveStatusRenderer.Build(manifestPath, manifest, workspace, agents, tools);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            return new Markup(
                $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                $"Live status error: {Markup.Escape(ex.Message)}[/]");
        }
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
