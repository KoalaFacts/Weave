using System.CommandLine;
using System.Globalization;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class WorkspaceUpCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var targetOption = new Option<string>("--target")
        {
            Description = "Deployment target",
            DefaultValueFactory = _ => "local"
        };
        targetOption.CompletionSources.Add(CliCompletions.CompleteDeployTargets);

        var cmd = new Command("up", "Start a workspace") { nameArg, targetOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var target = parseResult.GetValue(targetOption)!;

            var manifestPath = ManifestResolver.Resolve(name);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{name}'.");
                return 1;
            }

            CliTheme.WriteInfo($"Starting workspace from {manifestPath} (target: {target})...");

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var parser = new ManifestParser();
            var manifest = WorkspaceApiClient.PrepareManifest(
                parser.Parse(json),
                Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory());

            try
            {
                using var client = new WorkspaceApiClient();

                if (!await client.IsReachableAsync(cancellationToken))
                {
                    CliTheme.WriteError("Cannot reach the Weave server.");
                    CliTheme.WriteMuted("  Start it first with: weave serve");
                    return 1;
                }

                var response = await client.StartWorkspaceAsync(manifest, cancellationToken);
                var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                await File.WriteAllTextAsync(statePath, response.WorkspaceId, cancellationToken);

                CliTheme.WriteKeyValue("Workspace", manifest.Name);
                CliTheme.WriteKeyValue("Workspace ID", response.WorkspaceId);
                CliTheme.WriteKeyValue("Status", response.Status);
                CliTheme.WriteKeyValue("Agents", manifest.Agents.Count.ToString(CultureInfo.InvariantCulture));
                CliTheme.WriteKeyValue("Tools", manifest.Tools.Count.ToString(CultureInfo.InvariantCulture));
                CliTheme.WriteSuccess("Workspace started successfully.");
            }
            catch (Exception ex)
            {
                CliTheme.WriteError($"Failed to start workspace: {ex.Message}");
                return 1;
            }

            return 0;
        });

        return cmd;
    }
}

internal static class ManifestResolver
{
    public static string? Resolve(string? workspace)
    {
        // 1. Named workspace — look up in registry
        if (workspace is not null)
        {
            var registeredPath = WorkspaceRegistry.Resolve(workspace);
            if (registeredPath is not null)
            {
                var manifestPath = Path.Combine(registeredPath, "workspace.json");
                return File.Exists(manifestPath) ? manifestPath : null;
            }

            return null;
        }

        // 2. Current directory contains a manifest
        if (File.Exists("workspace.json"))
            return Path.GetFullPath("workspace.json");

        // 3. Walk up the directory tree
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "workspace.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
