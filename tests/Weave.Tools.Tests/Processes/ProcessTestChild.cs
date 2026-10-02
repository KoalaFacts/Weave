using System.Diagnostics;
using System.Globalization;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests.Processes;

internal sealed class ProcessTestChild : IDisposable
{
    private Process? _process;
    public string Root { get; } = Path.Join(Path.GetTempPath(), $"weave-child-{Guid.NewGuid():N}");
    public string PidPath => Path.Join(Root, "child.pid");
    public IReadOnlyList<string> Arguments { get; }
    public ToolInvocation Invocation { get; }

    public ProcessTestChild(string mode)
    {
        Directory.CreateDirectory(Root);
        var worker = Path.Join(AppContext.BaseDirectory, "Processes", "process-child.ts");
        Arguments = [worker, mode, Path.GetFileName(Root)];
        Invocation = new ToolInvocation
        {
            ToolName = "process-test",
            RawInput = "node " + string.Join(" ", new[]
            {
                Path.GetRelativePath(Environment.CurrentDirectory, worker), mode, Path.GetFileName(Root)
            }.Select(Quote)),
            Parameters = []
        };
    }

    public async Task<Process> GetProcessAsync(Task running)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!File.Exists(PidPath))
        {
            if (running.IsCompleted)
            {
                await running;
                throw new InvalidOperationException("The test child completed before publishing its PID.");
            }
            await Task.Delay(25, deadline.Token);
        }
        _process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(PidPath, deadline.Token), CultureInfo.InvariantCulture));
        return _process;
    }

    public void Release() => File.WriteAllText(Path.Join(Root, "go"), "go");

    private static string Quote(string value) => "'" + value.Replace("'", OperatingSystem.IsWindows() ? "''" : "'\\''", StringComparison.Ordinal) + "'";

    public void Dispose()
    {
        if (_process is not null)
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000).ShouldBeTrue("Test-owned child must exit before deleting its files.");
            _process.Dispose();
        }
        foreach (var file in Directory.EnumerateFiles(Root))
            File.Delete(file);
        Directory.Delete(Root);
    }
}
