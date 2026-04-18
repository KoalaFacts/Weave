using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Weave.Shared;
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
                    CliTheme.WriteInfo("Server not running — starting automatically...");
                    var started = await AutoStartServeAsync(cancellationToken);
                    if (!started)
                    {
                        CliTheme.WriteError("Could not start the Weave server.");
                        CliTheme.WriteMuted("  Start it manually with: weave serve");
                        return 1;
                    }

                    CliTheme.WriteSuccess("Server ready.");
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

    private static async Task<bool> AutoStartServeAsync(CancellationToken ct)
    {
        var siloPath = ResolveSiloPath();
        if (siloPath is null)
            return false;

        var port = CliConfigStore.Load().DefaultPort;

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || Directory.Exists(siloPath))
        {
            var project = siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? siloPath
                : Path.Combine(siloPath, "Weave.Silo.csproj");
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(project);
            startInfo.ArgumentList.Add("--");
        }
        else
        {
            startInfo.ArgumentList.Add(siloPath);
        }

        startInfo.ArgumentList.Add("--Weave:LocalMode=true");
        startInfo.ArgumentList.Add($"--urls=http://localhost:{port}");

        var config = CliConfigStore.Load();
        if (!string.IsNullOrWhiteSpace(config.Storage) && config.Storage != "memory")
        {
            startInfo.ArgumentList.Add($"--Weave:Storage={config.Storage}");

            if (!string.IsNullOrWhiteSpace(config.ConnectionString))
            {
                var connKey = config.Storage switch
                {
                    "postgresql" or "postgres" => "PostgreSql",
                    "sqlserver" => "SqlServer",
                    "redis" => "Redis",
                    _ => "Default"
                };
                startInfo.ArgumentList.Add($"--ConnectionStrings:{connKey}={config.ConnectionString}");
            }
        }

        Process.Start(startInfo);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500, ct);
            try
            {
                var response = await http.GetAsync($"http://localhost:{port}/health", ct);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch { }
        }

        return false;
    }

    private static string? ResolveSiloPath()
    {
        var envPath = Environment.GetEnvironmentVariable("WEAVE_SILO_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && (File.Exists(envPath) || Directory.Exists(envPath)))
            return envPath;

        var config = CliConfigStore.Load();
        if (!string.IsNullOrWhiteSpace(config.SiloPath) && (File.Exists(config.SiloPath) || Directory.Exists(config.SiloPath)))
            return config.SiloPath;

        var candidates = new[]
        {
            Path.Combine("src", "Runtime", "Weave.Silo"),
            Path.Combine("src", "Runtime", "Weave.Silo", "Weave.Silo.csproj")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var exeDir = AppContext.BaseDirectory;
        var siloDll = Path.Combine(exeDir, "Weave.Silo.dll");
        return File.Exists(siloDll) ? siloDll : null;
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
