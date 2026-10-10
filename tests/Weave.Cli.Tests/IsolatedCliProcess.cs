using System.Diagnostics;
using System.Text;

namespace Weave.Cli.Tests;

internal sealed class IsolatedCliProcess : IDisposable
{
    private readonly string _assemblyPath;
    public string Root { get; }
    public string WeaveHome => Path.Join(Root, ".weave");
    public string ConfigPath => Path.Join(WeaveHome, "config.json");

    public IsolatedCliProcess()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "These process tests require Linux HOME-based user-profile isolation.");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Weave.slnx")))
            directory = directory.Parent;
        directory.ShouldNotBeNull("The CLI process tests require the repository's Release build.");
        _assemblyPath = Path.Join(directory.FullName, "hosts", "Weave.Cli", "bin", "Release", "net10.0", "weave.dll");
        File.Exists(_assemblyPath).ShouldBeTrue("Build the Release CLI before running its process tests.");
        Root = Directory.CreateTempSubdirectory("weave-cli-process-").FullName;
    }

    public async Task AssertPrivateHomeAsync()
    {
        var result = await RunAsync("config", "get", "weaveHome");
        result.ExitCode.ShouldBe(0, result.StandardError);
        result.StandardOutput.Trim().ShouldBe(WeaveHome,
            "Child profile resolution must be isolated before any configuration write.");
    }

    public async Task<Result> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(_assemblyPath);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["HOME"] = Root;
        start.Environment["USERPROFILE"] = Root;
        start.Environment["XDG_CONFIG_HOME"] = Path.Join(Root, "config");
        start.Environment["XDG_DATA_HOME"] = Path.Join(Root, "data");
        start.Environment["XDG_CACHE_HOME"] = Path.Join(Root, "cache");
        start.Environment["DOTNET_CLI_HOME"] = Root;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["WEAVE_NO_UPDATE_CHECK"] = "1";
        start.Environment["WEAVE_API_URL"] = "http://127.0.0.1:1";
        start.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(start);
        process.ShouldNotBeNull();
        process.StandardInput.Close();
        var output = CaptureAsync(process.StandardOutput);
        var error = CaptureAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
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
            await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), output, error)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        return new Result(process.ExitCode, await output, await error);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private static async Task<string> CaptureAsync(StreamReader reader)
    {
        const int limit = 65536;
        var retained = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            var remaining = limit - retained.Length;
            retained.Append(buffer, 0, Math.Min(remaining, count));
            truncated |= count > remaining;
        }
        truncated.ShouldBeFalse("CLI test output exceeded the 65,536-character capture limit.");
        return retained.ToString();
    }

    internal sealed record Result(int ExitCode, string StandardOutput, string StandardError);
}
