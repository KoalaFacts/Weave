using System.Globalization;
using Weave.Actions.Context;
using Weave.Actions.SystemInfo;
using Weave.Actions.Workspace;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceUpCliCommand(
    StartWorkspaceAction startAction,
    GetSystemInfoAction systemInfoAction,
    IManifestResolver manifestResolver,
    WorkspacePrompt workspacePrompt,
    ISiloLauncher siloLauncher) : ICliCommand<WorkspaceUpOptions>
{
    public string Name => "up";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Start a workspace";

    public async Task<int> ExecuteAsync(WorkspaceUpOptions options, CancellationToken ct)
    {
        var name = workspacePrompt.SelectName(options.Name, "Which workspace would you like to start?");
        var manifestPath = manifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(name);
            return 1;
        }

        CliTheme.WriteInfo($"Starting workspace from {manifestPath} (target: {options.Target})...");

        var manifest = await WorkspaceManifestFile.ReadPreparedAsync(manifestPath, ct);

        var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (systemInfo.IsSuccess && !systemInfo.Value.Reachable)
        {
            CliTheme.WriteInfo("Server not running — starting automatically...");
            var started = await siloLauncher.AutoStartServeAsync(ct);
            if (!started)
            {
                if (ct.IsCancellationRequested)
                    return 130;

                CliTheme.WriteError("Could not start the Weave server.");
                CliTheme.WriteMuted("  Start it manually with: weave serve");
                return 1;
            }

            CliTheme.WriteSuccess("Server ready.");
        }

        var result = await startAction.ExecuteAsync(new StartWorkspaceInput(manifest), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;

            CliTheme.WriteError($"Failed to start workspace: {result.Failure.Message}");
            return 1;
        }

        var workspace = result.Value.Workspace;
        var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        await File.WriteAllTextAsync(statePath, workspace.WorkspaceId, ct);

        CliTheme.WriteKeyValue("Workspace", manifest.Name);
        CliTheme.WriteKeyValue("Workspace ID", workspace.WorkspaceId);
        CliTheme.WriteKeyValue("Status", workspace.Status);
        CliTheme.WriteKeyValue("Agents", manifest.Agents.Count.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Tools", manifest.Tools.Count.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteSuccess("Workspace started successfully.");

        return 0;
    }
}
