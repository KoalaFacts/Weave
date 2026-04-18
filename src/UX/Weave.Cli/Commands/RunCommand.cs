using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using Weave.Shared;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class RunCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name (defaults to current directory)",
            DefaultValueFactory = _ => null,
            Arity = ArgumentArity.ZeroOrOne
        };
        var portOption = new Option<int>("--port")
        {
            Description = "Server port",
            DefaultValueFactory = _ => CliConfigStore.Load().DefaultPort
        };

        var cmd = new Command("run", "Start the server and workspace in one command") { nameArg, portOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var port = parseResult.GetValue(portOption);

            var manifestPath = ManifestResolver.Resolve(name);

            // Guided mode: no workspace found — help the user pick or create one
            if (manifestPath is null)
            {
                CliTheme.WriteBanner();

                var existing = WorkspaceRegistry.GetAll();
                if (existing.Count > 0)
                {
                    CliTheme.WriteInfo(name is null
                        ? "No workspace.json found in the current directory."
                        : $"Workspace '{name}' not found.");
                    AnsiConsole.WriteLine();

                    var choices = existing.Select(w => w.Key).ToList();
                    choices.Add("Create a new workspace");

                    var picked = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Which workspace would you like to run?")
                            .Styled()
                            .AddChoices(choices));

                    if (picked == "Create a new workspace")
                    {
                        CliTheme.WriteMuted("  Run: weave workspace new <name>");
                        return 0;
                    }

                    name = picked;
                    manifestPath = ManifestResolver.Resolve(name);
                }
                else
                {
                    CliTheme.WriteInfo("No workspaces found. Let's create one.");
                    AnsiConsole.WriteLine();

                    var wsName = AnsiConsole.Prompt(
                        new TextPrompt<string>("Workspace name:")
                            .Styled()
                            .DefaultValue("my-workspace"));

                    var preset = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Choose a preset:")
                            .Styled()
                            .AddChoices([.. WorkspacePresets.All.Keys]));

                    CliTheme.WriteMuted($"  Creating workspace '{wsName}' with preset '{preset}'...");
                    CliTheme.WriteMuted($"  Run: weave workspace new {wsName} --preset {preset}");
                    CliTheme.WriteMuted($"  Then: weave run {wsName}");
                    return 0;
                }

                if (manifestPath is null)
                {
                    CliTheme.WriteError($"Workspace '{name}' exists but has no workspace.json.");
                    return 1;
                }
            }

            CliTheme.WriteBanner();

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var parser = new ManifestParser();
            var manifest = WorkspaceApiClient.PrepareManifest(
                parser.Parse(json),
                Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory());

            CliTheme.WriteKeyValue("Workspace", manifest.Name);
            CliTheme.WriteKeyValue("Agents", manifest.Agents.Count.ToString(CultureInfo.InvariantCulture));
            CliTheme.WriteKeyValue("Tools", manifest.Tools.Count.ToString(CultureInfo.InvariantCulture));
            if (manifest.Channels.Count > 0)
                CliTheme.WriteKeyValue("Channels", manifest.Channels.Count.ToString(CultureInfo.InvariantCulture));
            AnsiConsole.WriteLine();

            var serverAlreadyRunning = await IsReachableAsync(port, cancellationToken);
            Process? siloProcess = null;

            if (!serverAlreadyRunning)
            {
                CliTheme.WriteInfo($"Starting server on port {port}...");

                var siloPath = ResolveSiloPath();
                if (siloPath is null)
                {
                    CliTheme.WriteError("Could not locate the Weave silo.");
                    CliTheme.WriteMuted("  Run `weave init` to configure, or set WEAVE_SILO_PATH.");
                    return 1;
                }

                siloProcess = StartSilo(siloPath, port, manifest.Workspace.Storage);
                if (siloProcess is null)
                {
                    CliTheme.WriteError("Failed to start server.");
                    return 1;
                }

                var ready = await WaitForReadyAsync(port, cancellationToken);
                if (!ready)
                {
                    CliTheme.WriteError("Server did not become ready in time.");
                    TryKill(siloProcess);
                    return 1;
                }

                CliTheme.WriteSuccess("Server ready.");
            }
            else
            {
                CliTheme.WriteInfo("Server already running.");
            }

            try
            {
                using var client = new WorkspaceApiClient($"http://localhost:{port}");
                var response = await client.StartWorkspaceAsync(manifest, cancellationToken);

                var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                await File.WriteAllTextAsync(statePath, response.WorkspaceId, cancellationToken);

                AnsiConsole.WriteLine();
                CliTheme.WriteSuccess($"Workspace \"{manifest.Name}\" is running.");
                CliTheme.WriteKeyValue("Workspace ID", response.WorkspaceId);
                CliTheme.WriteKeyValue("API", $"http://localhost:{port}");
                CliTheme.WriteKeyValue("Dashboard", $"http://localhost:{port + 1}");
                AnsiConsole.WriteLine();
                CliTheme.WriteMuted("  Press Ctrl+C to stop.");

                if (siloProcess is not null)
                    await siloProcess.WaitForExitAsync(cancellationToken);
                else
                    await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.WriteLine();
                CliTheme.WriteInfo("Shutting down...");
            }
            catch (Exception ex)
            {
                CliTheme.WriteError($"Failed to start workspace: {ex.Message}");
                TryKill(siloProcess);
                return 1;
            }
            finally
            {
                TryKill(siloProcess);
            }

            return 0;
        });

        return cmd;
    }

    private static Process? StartSilo(string siloPath, int port, Weave.Workspaces.Models.StorageConfig? workspaceStorage = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(siloPath))
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

        var storageBackend = workspaceStorage?.Backend;
        var storageConn = workspaceStorage?.ConnectionString;
        var storageSchema = workspaceStorage?.Schema;

        var cliConfig = CliConfigStore.Load();

        if (string.IsNullOrWhiteSpace(storageBackend))
        {
            storageBackend = cliConfig.Storage;
            storageConn = CliConfigStore.ResolveConnectionString(cliConfig.ConnectionString);
        }
        else if (!string.IsNullOrWhiteSpace(storageConn))
        {
            storageConn = CliConfigStore.ResolveConnectionString(storageConn);
        }

        if (!string.IsNullOrWhiteSpace(storageBackend) && storageBackend != "memory")
        {
            startInfo.ArgumentList.Add($"--Weave:Storage={storageBackend}");

            if (!string.IsNullOrWhiteSpace(storageConn))
            {
                var connKey = storageBackend switch
                {
                    "postgresql" or "postgres" => "PostgreSql",
                    "sqlserver" => "SqlServer",
                    "redis" => "Redis",
                    "sqlite" => "Sqlite",
                    _ => "Default"
                };
                startInfo.ArgumentList.Add($"--ConnectionStrings:{connKey}={storageConn}");
            }

            if (!string.IsNullOrWhiteSpace(storageSchema))
                startInfo.ArgumentList.Add($"--Weave:StorageSchema={storageSchema}");

            if (!string.IsNullOrWhiteSpace(workspaceStorage?.Database))
                startInfo.ArgumentList.Add($"--Weave:StorageDatabase={workspaceStorage.Database}");
        }

        // Auth config from CLI settings
        if (!string.IsNullOrWhiteSpace(cliConfig.AuthMode) && cliConfig.AuthMode != "none")
        {
            startInfo.ArgumentList.Add($"--Weave:Auth:Mode={cliConfig.AuthMode}");
            var resolvedAuth = CliConfigStore.ResolveConnectionString(cliConfig.AuthSecret);
            if (!string.IsNullOrWhiteSpace(resolvedAuth))
                startInfo.ArgumentList.Add($"--Weave:Auth:Secret={resolvedAuth}");
        }

        if (cliConfig.RequireHttps)
            startInfo.ArgumentList.Add("--Weave:RequireHttps=true");

        return Process.Start(startInfo);
    }

    private static async Task<bool> IsReachableAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync($"http://localhost:{port}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForReadyAsync(int port, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500, ct);
            if (await IsReachableAsync(port, ct))
                return true;
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
        if (File.Exists(siloDll))
            return siloDll;

        return null;
    }

    private static void TryKill(Process? process)
    {
        if (process is null || process.HasExited)
            return;
        try { process.Kill(entireProcessTree: true); } catch { }
    }
}
