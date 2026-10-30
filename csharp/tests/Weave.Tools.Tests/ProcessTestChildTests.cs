using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Invocations.Processes;
using Weave.Tools.Tests.Processes;

namespace Weave.Tools.Tests;

public sealed class ProcessTestChildTests
{
    [Fact]
    public async Task GetProcessAsync_PublishedPidHasSharedPublisherHandle_ReadsWhileChildRemainsAlive()
    {
        using var child = new ProcessTestChild("wait");
        var runner = new ProcessRunner(TimeProvider.System, NullLogger<ProcessRunner>.Instance);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = runner.RunAsync("node", child.Arguments, abort.Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(child.PidPath))
                await Task.Delay(10, deadline.Token);
            using var publisher = new FileStream(child.PidPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            var actual = await child.GetProcessAsync(running);
            actual.HasExited.ShouldBeFalse();
            running.IsCompleted.ShouldBeFalse();
            using var expected = new StreamReader(publisher, leaveOpen: true);
            actual.Id.ToString(CultureInfo.InvariantCulture).ShouldBe(await expected.ReadToEndAsync(deadline.Token));
        }
        finally
        {
            await abort.CancelAsync();
            var canceled = await Should.ThrowAsync<OperationCanceledException>(() =>
                running.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            canceled.CancellationToken.ShouldBe(abort.Token);
        }
    }
}
