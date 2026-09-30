using System.Diagnostics;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

/// <summary>
/// Exercises <see cref="ProcessCommandRunner"/> against real child processes.
/// Uses <c>dotnet --version</c> as a cross-platform happy-path probe — the
/// runner executes in CI on Windows/Linux/macOS and <c>dotnet</c> is always
/// on PATH. The failure path invokes a deliberately bad command to verify
/// the non-zero exit-code → <see cref="InvalidOperationException"/> contract.
/// </summary>
public sealed class ProcessCommandRunnerTests
{
    private readonly ProcessCommandRunner _runner = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ExcessOutput_RejectsInsteadOfRetainingUnboundedOutput(bool standardError)
    {
        var command = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/d", "/c", "for /L %i in (1,1,20000) do @echo 1234567890" + (standardError ? " 1>&2" : "")]
            : ["-c", "i=0; while [ $i -lt 20000 ]; do printf '1234567890\\n'; i=$((i+1)); done" + (standardError ? " >&2" : "")];

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            _runner.RunAsync(command, arguments, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("output limit");
        error.Message.ShouldNotContain("1234567890");
    }

    [Fact]
    public async Task RunAsync_Cancelled_KillsActualChildProcess()
    {
        var pidPath = Path.Join(Path.GetTempPath(), $"weave-cli-{Guid.NewGuid():N}.pid");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var command = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-Command", $"$PID | Set-Content -LiteralPath '{pidPath.Replace("'", "''")}'; Start-Sleep -Seconds 60"]
                : ["-c", "echo $$ > \"$1\"; sleep 60", "--", pidPath];
            var running = _runner.RunAsync(command, arguments, cancellation.Token);
            var pid = 0;
            for (var attempt = 0; attempt < 200 && pid == 0; attempt++)
            {
                if (running.IsCompleted)
                    await running;
                if (File.Exists(pidPath)
                    && !int.TryParse(await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken), out pid))
                    pid = 0;
                if (pid == 0)
                    await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            pid.ShouldBeGreaterThan(0);
            using var child = Process.GetProcessById(pid);
            try
            {
                await cancellation.CancelAsync();
                await Should.ThrowAsync<OperationCanceledException>(() => running);
                await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                child.HasExited.ShouldBeTrue();
            }
            finally
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsStdout()
    {
        var output = await _runner.RunAsync("dotnet", ["--version"], TestContext.Current.CancellationToken);

        output.ShouldNotBeNullOrWhiteSpace();
        output.Trim().ShouldMatch(@"^\d+\.\d+\.\d+"); // "10.0.x" or similar
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ThrowsInvalidOperationWithExitCode()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _runner.RunAsync("dotnet", ["--definitely-not-a-flag"], TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("failed");
        ex.Message.ShouldContain("exit code");
        ex.Message.ShouldNotContain("--definitely-not-a-flag");
    }

    [Fact]
    public async Task RunAsync_UnknownExecutable_ThrowsWin32Exception()
    {
        await Should.ThrowAsync<System.ComponentModel.Win32Exception>(
            () => _runner.RunAsync("this-binary-definitely-does-not-exist-on-path-xyz123", [], TestContext.Current.CancellationToken));
    }
}
