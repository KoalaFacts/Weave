using System.Diagnostics;
using System.Text;

namespace Weave.Cli.Tests;

internal static class SiloLauncherProcessHarness
{
    private const string RootVariable = "WEAVE_TEST_SILO_LAUNCH_ROOT";
    private const string ClassVariable = "WEAVE_TEST_SILO_LAUNCH_CLASS";

    public static async Task RunAsync(Type testClass, Func<string, Task> scenario)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Silo launcher process fixtures require Linux HOME isolation and executable scripts.");
        var childRoot = Environment.GetEnvironmentVariable(RootVariable);
        if (childRoot is not null)
        {
            Environment.GetEnvironmentVariable(ClassVariable).ShouldBe(testClass.FullName);
            Path.IsPathFullyQualified(childRoot).ShouldBeTrue();
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ShouldBe(childRoot);
            await SiloLauncherProcessGroup.AssertOwnGroupAsync(childRoot);
            Directory.SetCurrentDirectory(childRoot);
            Environment.CurrentDirectory.ShouldBe(childRoot);
            await scenario(childRoot);
            await File.WriteAllTextAsync(Path.Join(childRoot, "scenario-complete"), testClass.Name,
                TestContext.Current.CancellationToken);
            return;
        }

        var root = Directory.CreateTempSubdirectory("weave-launcher-").FullName;
        int? processGroup = null;
        Process? ownedProcess = null;
        try
        {
            File.Exists("/usr/bin/setsid").ShouldBeTrue("Linux setsid is required to own all launcher descendants.");
            var start = new ProcessStartInfo("/usr/bin/setsid")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--wait");
            start.ArgumentList.Add(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
            start.ArgumentList.Add(testClass.Assembly.Location);
            start.ArgumentList.Add("-class");
            start.ArgumentList.Add(testClass.FullName.ShouldNotBeNull());
            start.Environment[RootVariable] = root;
            start.Environment[ClassVariable] = testClass.FullName;
            start.Environment["HOME"] = root;
            start.Environment["USERPROFILE"] = root;
            start.Environment["XDG_CONFIG_HOME"] = Path.Join(root, "config");
            start.Environment["XDG_DATA_HOME"] = Path.Join(root, "data");
            start.Environment["XDG_CACHE_HOME"] = Path.Join(root, "cache");
            start.Environment["DOTNET_CLI_HOME"] = root;
            start.Environment["DOTNET_NOLOGO"] = "1";
            start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["WEAVE_NO_UPDATE_CHECK"] = "1";
            start.Environment.Remove("WEAVE_SILO_PATH");
            start.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
            start.Environment["no_proxy"] = "localhost,127.0.0.1,::1";
            var process = Process.Start(start);
            process.ShouldNotBeNull();
            ownedProcess = process;
            processGroup = process.Id;
            File.WriteAllText(Path.Join(root, "group-owner.pending"), process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(Path.Join(root, "group-owner.pending"), Path.Join(root, "group-owner"));
            process.StandardInput.Close();
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), stdout, stderr)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            process.ExitCode.ShouldBe(0, (await stdout) + (await stderr));
            File.ReadAllText(Path.Join(root, "scenario-complete")).ShouldBe(testClass.Name);
        }
        finally
        {
            if (ownedProcess is not null)
            {
                try
                {
                    if (!ownedProcess.HasExited)
                        ownedProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (ownedProcess.HasExited)
                {
                    await ownedProcess.WaitForExitAsync(CancellationToken.None);
                }
                await ownedProcess.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (processGroup is { } group)
                await SiloLauncherProcessGroup.StopAsync(group);
            ownedProcess?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    public static SiloLauncher Create(CliConfig config) => new(new FixedConfig(config), new NoSecrets());

    internal static async Task<string> DrainAsync(StreamReader reader)
    {
        var retained = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            retained.Append(buffer, 0, count);
            if (retained.Length > 16384)
                retained.Remove(0, retained.Length - 16384);
        }
        return retained.ToString();
    }

    private sealed class FixedConfig(CliConfig config) : IConfigStore
    {
        public CliConfig Load() => config;
        public bool Exists() => true;
        public void Save(CliConfig value) => throw new InvalidOperationException("Launching must not write CLI configuration.");
    }

    private sealed class NoSecrets : ISecretResolver
    {
        public string? ResolveReference(string? reference) => throw new InvalidOperationException("Memory storage must not resolve credentials.");
        public string ToEnvReference(string storageBackend) => throw new InvalidOperationException("Launching must not create credential references.");
    }
}
