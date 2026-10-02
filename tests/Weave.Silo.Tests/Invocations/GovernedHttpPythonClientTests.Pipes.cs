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
            using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker, startup.Token),
                System.Globalization.CultureInfo.InvariantCulture));
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
}
