using System.Diagnostics;
using System.Text.Json;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Fact]
    public async Task RunPythonAsync_LargeDualPipeOutput_DrainsBothStreamsBeforeExit()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        const string script = "import sys; sys.stdout.write('o' * 131072); sys.stdout.flush(); sys.stderr.write('e' * 131072); sys.stderr.flush()";
        var result = await RunPythonAsync(script, "", timeout.Token);
        result.ExitCode.ShouldBe(0);
        result.Output.ShouldBe(new string('o', 131072));
        result.Error.ShouldBe(new string('e', 131072));
        result.ReaderWasThreadPool.ShouldBe([false, false]);
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task RunPythonAsync_OutputExceedsLimit_DrainsAndRejects(string stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var script = $"import sys; sys.{stream}.write('x' * {PythonOutputLimit + 1}); sys.{stream}.flush()";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunPythonAsync(script, "", timeout.Token));
        error.Message.ShouldBe("Python client output exceeded the test capture limit.");
    }

    [Fact]
    public async Task RunPythonAsync_CancelledAfterChildStarts_TerminatesChildAndObservesReaders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"weave-python-pipes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "child.pid");
        const string script = """
            import json, os, sys, time
            marker = json.load(sys.stdin)['marker']
            with open(marker + '.pending', 'w') as handle:
                handle.write(str(os.getpid()))
            os.replace(marker + '.pending', marker)
            while True:
                time.sleep(1)
            """;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        abort.CancelAfter(TimeSpan.FromSeconds(45));
        var pending = RunPythonAsync(script, JsonSerializer.Serialize(new { marker }), abort.Token);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker))
            {
                pending.IsCompleted.ShouldBeFalse("The child must publish its PID before ending.");
                await Task.Delay(TimeSpan.FromMilliseconds(20), startup.Token);
            }
            using var child = Process.GetProcessById(await ReadPythonChildPidAsync(marker, startup.Token));
            child.HasExited.ShouldBeFalse();
            abort.Cancel();
            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            cancelled.CancellationToken.ShouldBe(abort.Token);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            child.HasExited.ShouldBeTrue();
        }
        finally
        {
            abort.Cancel();
            // Cancellation is expected; completion includes child termination and both reader tasks.
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReadPythonChildPidAsync_MarkerLocked_WaitsForRelease()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows sharing violations require a Windows fixture.");
        var marker = Path.GetTempFileName();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await File.WriteAllTextAsync(marker, "12345", timeout.Token);
            Task<int> pending;
            using (File.Open(marker, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                pending = ReadPythonChildPidAsync(marker, timeout.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
                pending.IsCompleted.ShouldBeFalse("A temporary sharing violation must wait for the marker to become readable.");
            }
            (await pending).ShouldBe(12345);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task ReadPythonChildPidAsync_MarkerLocked_CancellationStopsWait()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows sharing violations require a Windows fixture.");
        var marker = Path.GetTempFileName();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            using var locked = File.Open(marker, FileMode.Open, FileAccess.Read, FileShare.None);
            var pending = ReadPythonChildPidAsync(marker, abort.Token);
            abort.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            error.CancellationToken.ShouldBe(abort.Token);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task ReadPythonChildPidAsync_MalformedMarker_FailsWithoutRetry()
    {
        var marker = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(marker, "invalid-pid", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<FormatException>(() => ReadPythonChildPidAsync(marker, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task ReadPythonChildPidAsync_MissingMarker_FailsWithoutRetry()
    {
        var marker = Path.Join(Path.GetTempPath(), $"weave-missing-pid-{Guid.NewGuid():N}");
        await Assert.ThrowsAsync<FileNotFoundException>(() => ReadPythonChildPidAsync(marker, TestContext.Current.CancellationToken));
    }

    private static async Task<int> ReadPythonChildPidAsync(string marker, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return int.Parse(await File.ReadAllTextAsync(marker, ct), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xFFFF) is 32 or 33)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
            }
        }
    }
}
