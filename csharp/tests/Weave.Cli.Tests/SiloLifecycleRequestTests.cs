using System.Diagnostics;
using System.Text;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLifecycleRequestTests
{
    [Fact]
    public Task RequestAsync_WorkerExitsBeforeRequest_ReportsExit() => WithWorkerAsync("exit", async (worker, ct) =>
    {
        worker.AllowExit();
        await worker.WaitForExitAsync(ct);

        var operation = new TaskCompletionSource();
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => worker.RequestAsync(operation.Task, ct));

        failure.Message.ShouldContain("exit code 23");
        ct.IsCancellationRequested.ShouldBeFalse();
    });

    [Fact]
    public Task RequestAsync_CommandCompletesBeforeRequest_ReportsProgress() => WithWorkerAsync("healthy", async (worker, ct) =>
    {
        await worker.ListeningAsync(ct);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => worker.RequestAsync(Task.CompletedTask, ct));

        failure.Message.ShouldContain("Command finished before the workspace request");
        failure.Message.ShouldContain("started=True; listening=True; health=False; log=True");
        worker.HasExited.ShouldBeFalse();
        ct.IsCancellationRequested.ShouldBeFalse();
    });

    [Fact]
    public Task RequestAsync_CommandFailsBeforeRequest_PreservesFailure() => WithWorkerAsync("healthy", async (worker, ct) =>
    {
        var original = new IOException("command-startup-failure-marker");

        var failure = await Should.ThrowAsync<IOException>(() => worker.RequestAsync(Task.FromException(original), ct));

        failure.ShouldBeSameAs(original);
        ct.IsCancellationRequested.ShouldBeFalse();
    });

    [Fact]
    public Task RequestAsync_DeadlineCancelled_ReportsProgressAndPreservesToken() => WithWorkerAsync("healthy", async (worker, ct) =>
    {
        await worker.ListeningAsync(ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.Cancel();
        var operation = new TaskCompletionSource();

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => worker.RequestAsync(operation.Task, cancellation.Token));

        failure.CancellationToken.ShouldBe(cancellation.Token);
        failure.Message.ShouldContain("started=True; listening=True; health=False; log=True");
        worker.HasExited.ShouldBeFalse();
    });

    [Fact]
    public Task RequestAsync_RequestRecorded_ReturnsRequest() => WithWorkerAsync("healthy", async (worker, ct) =>
    {
        await worker.ListeningAsync(ct);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var body = new StringContent("{\"name\":\"request-observation-control\"}", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri($"http://127.0.0.1:{worker.Port}/api/workspaces"), body, ct);
        response.EnsureSuccessStatusCode();

        var request = await worker.RequestAsync(Task.CompletedTask, ct);

        request["body"].ShouldNotBeNull()["name"].ShouldNotBeNull().GetValue<string>()
            .ShouldBe("request-observation-control");
        worker.HasExited.ShouldBeFalse();
    });

    private static async Task WithWorkerAsync(string mode, Func<SiloLifecycleWorker, CancellationToken, Task> scenario)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Lifecycle fixture requires Linux executable scripts.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var root = Directory.CreateTempSubdirectory("weave-request-observation-").FullName;
        try
        {
            await using var worker = new SiloLifecycleWorker(Path.Join(root, "worker"), mode);
            worker.ReleasePort();
            var start = new ProcessStartInfo(worker.Executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add($"--urls=http://localhost:{worker.Port}");
            var process = Process.Start(start).ShouldNotBeNull();
            worker.Attach(process);
            SiloProcessService.AttachLogDrainer(process, Path.Join(root, ".weave", "silo.log"));
            await scenario(worker, deadline.Token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
