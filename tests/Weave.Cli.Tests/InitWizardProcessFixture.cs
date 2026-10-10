using System.Diagnostics;
using System.Text.Json;

namespace Weave.Cli.Tests;

internal sealed class InitWizardProcessFixture : IDisposable
{
    private static readonly string[] ProfileVariables = ["HOME", "USERPROFILE", "DOTNET_CLI_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "WEAVE_NO_UPDATE_CHECK", "WEAVE_PG_CONNECTION", "WEAVE_API_SECRET", "GITHUB_ACTIONS"];

    public string Root { get; } = Directory.CreateTempSubdirectory("weave-init-pty-").FullName;
    public string Profile => Path.Join(Root, "profile");
    public string Workspace => Path.Join(Root, "workspace");
    public string Private => Path.Join(Profile, ".weave");
    public string ConfigPath => Path.Join(Private, "config.json");
    public string ExplicitRuntime => Path.Join(Root, "runtime with spaces", "Weave.Silo");
    public string DetectedRuntime => Path.Join(Workspace, "hosts", "Weave.Host");

    public InitWizardProcessFixture()
    {
        Directory.CreateDirectory(Private);
        Directory.CreateDirectory(Workspace);
    }

    public async Task<JsonDocument> RunAsync(string scenario)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Init wizard interaction requires Linux PTYs and HOME-based profile isolation.");
        var python = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Join(directory, "python3")).FirstOrDefault(File.Exists);
        Assert.SkipWhen(python is null, "python3 is required for the real init terminal boundary.");
        python.ShouldNotBeNull();
        var executable = Path.Join(AppContext.BaseDirectory, "weave");
        File.Exists(executable).ShouldBeTrue("Build the CLI project reference before running init process tests.");
        File.Exists(Path.Join(AppContext.BaseDirectory, "host", "Weave.Silo")).ShouldBeFalse("Runtime detection requires an unbundled test build.");
        File.Exists(Path.Join(AppContext.BaseDirectory, "Weave.Silo.dll")).ShouldBeFalse("Runtime detection requires an unbundled test build.");
        var parentDirectory = Environment.CurrentDirectory;
        var parentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parentEnvironment = ProfileVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Root
        };
        foreach (var argument in new[] { "-u", "-c", InitWizardPtyDriver.Script, executable, Root, scenario })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        var started = false;
        try
        {
            started = process.Start();
            started.ShouldBeTrue();
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.ShouldBe(0, await stderr);
            var result = JsonDocument.Parse(await stdout);
            result.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(0);
            Environment.CurrentDirectory.ShouldBe(parentDirectory);
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ShouldBe(parentProfile);
            foreach (var (name, value) in parentEnvironment)
                Environment.GetEnvironmentVariable(name).ShouldBe(value);
            return result;
        }
        finally
        {
            if (started)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanup.Token);
                if (stdout is not null && stderr is not null)
                    await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token);
            }
        }
    }

    public JsonDocument ReadConfig()
    {
        File.Exists(ConfigPath).ShouldBeTrue("A successful init must persist the private configuration.");
        return JsonDocument.Parse(File.ReadAllText(ConfigPath));
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
