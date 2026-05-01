using Spectre.Console;
using Spectre.Console.Rendering;
using Weave.Cli.Commands;
using Weave.Workspaces.Models;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from the TUI shell.")]
internal sealed class TuiLiveStatusView
{
    private readonly TuiLiveStatusRenderer _renderer = new();

    public async Task<bool> TryRenderOnceAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var workspaceId = ReadWorkspaceId(manifestPath);
        if (workspaceId is null)
            return false;

        try
        {
            using var client = new WorkspaceApiClient();
            if (!await client.IsReachableAsync(cancellationToken))
                return false;

            var workspace = await client.GetWorkspaceAsync(workspaceId, cancellationToken);
            var agents = await client.GetAgentsAsync(workspaceId, cancellationToken);
            var tools = await client.GetToolsAsync(workspaceId, cancellationToken);

            AnsiConsole.Write(_renderer.Build(manifestPath, manifest, workspace, agents, tools));
            return true;
        }
        catch (Exception ex)
        {
            CliTheme.WriteWarning($"Live status unavailable: {ex.Message}. Showing manifest instead.");
            return false;
        }
    }

    public async Task WatchAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var workspaceId = ReadWorkspaceId(manifestPath);
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
                .StartAsync(async ctx => await WatchLoopAsync(ctx, client, workspaceId, manifestPath, manifest, lifetime));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WatchLoopAsync(
        LiveDisplayContext ctx,
        WorkspaceApiClient client,
        string workspaceId,
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationTokenSource lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            ctx.UpdateTarget(await BuildWatchFrameAsync(client, workspaceId, manifestPath, manifest, lifetime.Token));

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
        CancellationToken ct)
    {
        try
        {
            if (!await client.IsReachableAsync(ct))
            {
                return new Markup(
                    $"[rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                    $"Silo is not reachable. Retrying…[/]");
            }

            var workspace = await client.GetWorkspaceAsync(workspaceId, ct);
            var agents = await client.GetAgentsAsync(workspaceId, ct);
            var tools = await client.GetToolsAsync(workspaceId, ct);
            return new TuiLiveStatusRenderer().Build(manifestPath, manifest, workspace, agents, tools);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
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
            $"Auto-refresh every 2s · press any key to return[/]");
        AnsiConsole.WriteLine();
    }

    private static string? ReadWorkspaceId(string manifestPath)
    {
        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        if (!File.Exists(statePath))
            return null;

        try
        {
            var id = File.ReadAllText(statePath).Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (IOException)
        {
            return null;
        }
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
