using System.Diagnostics;
using System.Text.Json;

namespace Weave.Cli.Tests;

[Collection("Tui PTY process")]
[Trait("Category", "Integration")]
public sealed class TuiTerminalProcessTests
{
    private static readonly string[] ProfileVariables = ["HOME", "USERPROFILE", "DOTNET_CLI_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "WEAVE_NO_UPDATE_CHECK"];

    [Fact]
    public async Task Tui_RealTerminalCommands_EditInputOpenWorkspaceAndExitCleanly()
    {
        using var result = await RunAsync("commands");

        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldContain("Workspace · pty-workspace");
        output.ShouldContain("/use <agent>");
        output.ShouldContain("anything without a leading slash is sent to the current agent");
        output.ShouldContain("Workspace 'pty-workspace' is not running. Type /up to start it.");
        output.ShouldContain("No conversation history yet. Send a message first.");
        output.ShouldNotContain("Unknown command:");
        output.ShouldContain("See you next weave.");
        result.RootElement.GetProperty("stages").EnumerateArray().Select(stage => stage.GetString())
            .ShouldBe(["ready", "isolated-profile", "open", "corrected-help", "stopped-message", "history", "quit"]);
    }

    [Fact]
    public async Task Tui_WatchLiveWorkspace_PollsFreshSnapshotsAndReturnsToComposerOnKey()
    {
        using var result = await RunAsync("watch");

        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldContain("Watching · pty-workspace");
        output.ShouldContain("press any key to return");
        output.ShouldContain("pty-live-id");
        output.ShouldContain("poll-model-2");
        output.ShouldContain("poll-model-3");
        output.ShouldContain("live-files");
        output.ShouldNotContain("Live status error:");
        output.ShouldContain("See you next weave.");
        var requests = result.RootElement.GetProperty("watchRequests").EnumerateArray().Select(request => request.GetString()).ToArray();
        requests.Count(request => request == "GET /api/workspaces/pty-live-id").ShouldBeGreaterThanOrEqualTo(2);
        requests.Count(request => request == "GET /api/workspaces/pty-live-id/agents").ShouldBeGreaterThanOrEqualTo(2);
        requests.Count(request => request == "GET /api/workspaces/pty-live-id/tools").ShouldBeGreaterThanOrEqualTo(2);
        result.RootElement.GetProperty("stages").EnumerateArray().Select(stage => stage.GetString())
            .ShouldBe(["ready", "isolated-profile", "open-live", "refreshed-live", "watch-return", "quit"]);
    }

    [Fact]
    public async Task ZeroArguments_RealTerminal_RequiresSecondControlCToExit()
    {
        using var result = await RunAsync("cancel");

        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldContain("Ctrl+C again to exit");
        output.ShouldContain("See you next weave.");
        result.RootElement.GetProperty("stages").EnumerateArray().Select(stage => stage.GetString())
            .ShouldBe(["ready", "isolated-profile", "armed", "confirmed"]);
    }

    private static async Task<JsonDocument> RunAsync(string scenario)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "This test exercises Linux PTYs and /proc through Python's standard library.");
        var python = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Join(directory, "python3")).FirstOrDefault(File.Exists);
        Assert.SkipWhen(python is null, "python3 is required for the real terminal boundary test.");
        python.ShouldNotBeNull();
        var executable = Path.Join(AppContext.BaseDirectory, "weave");
        File.Exists(executable).ShouldBeTrue("Build the CLI project reference before running this test.");
        var parentDirectory = Environment.CurrentDirectory;
        var parentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parentEnvironment = ProfileVariables
            .ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        var root = Directory.CreateTempSubdirectory("weave-tui-pty-").FullName;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        foreach (var argument in new[] { "-u", "-c", TuiPtyDriver.Script, executable, root, scenario })
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
            result.RootElement.GetProperty("home").GetString().ShouldBe(Path.Join(root, "profile"));
            result.RootElement.GetProperty("cwd").GetString().ShouldBe(Path.Join(root, "workspace"));
            var output = result.RootElement.GetProperty("output").GetString();
            output.ShouldNotBeNull();
            output.ShouldContain(Path.Join(root, "profile", ".weave"));
            var requests = result.RootElement.GetProperty("requests").EnumerateArray().Select(request => request.GetString()).ToArray();
            requests.ShouldNotBeEmpty();
            if (scenario == "watch")
                requests.ShouldAllBe(request => request == "GET /health" || request == "GET /api/workspaces/pty-live-id"
                    || request == "GET /api/workspaces/pty-live-id/agents" || request == "GET /api/workspaces/pty-live-id/tools");
            else
                requests.ShouldAllBe(request => request == "GET /health");
            Environment.CurrentDirectory.ShouldBe(parentDirectory);
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ShouldBe(parentProfile);
            foreach (var (name, value) in parentEnvironment)
                Environment.GetEnvironmentVariable(name).ShouldBe(value);
            return result;
        }
        finally
        {
            try
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
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
