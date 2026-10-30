using System.Diagnostics;
using System.Net;
using Weave.Mailbox.Host;

namespace Weave.Mailboxes.Tests.Http;

[Trait("Category", "Integration")]
public sealed class MailboxProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Main_ExplicitHashedControlAndRetainedDatabase_RealProcessServesOwnedInbox()
    {
        await using var context = new MailboxHttpContext();
        using var process = new Process { StartInfo = HostStart(context) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(MailboxHttpContext.Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Start().ShouldBeTrue();
        var stdout = Drain(process.StandardOutput, listening, timeout.Token);
        var stderr = Drain(process.StandardError, null, timeout.Token);
        Exception? operationFailure = null;
        try
        {
            using var client = new HttpClient { BaseAddress = await listening.Task.WaitAsync(timeout.Token) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/inbox");
            request.Headers.Add(MailboxControlAuthentication.HeaderName, context.Secret("bob"));
            using var response = await client.SendAsync(request, timeout.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(timeout.Token)).ShouldBe("{\"items\":[],\"nextCursor\":null}");
        }
        catch (Exception error)
        {
            operationFailure = error;
            throw;
        }
        finally
        {
            await StopHost(process, stdout, stderr, operationFailure, timeout.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopHost_CancelledLiveOperation_ObservesCleanupAndPreservesCancellation(bool expires)
    {
        await using var context = new MailboxHttpContext();
        using var process = new Process { StartInfo = HostStart(context) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(MailboxHttpContext.Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Start().ShouldBeTrue();
        var stdout = Drain(process.StandardOutput, listening, timeout.Token);
        var stderr = Drain(process.StandardError, null, timeout.Token);
        OperationCanceledException? operationFailure = null;
        var liveVerified = false;

        async Task CancelLiveOperation()
        {
            try
            {
                using var client = new HttpClient { BaseAddress = await listening.Task.WaitAsync(timeout.Token) };
                using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/inbox");
                request.Headers.Add(MailboxControlAuthentication.HeaderName, context.Secret("bob"));
                using var response = await client.SendAsync(request, timeout.Token);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                process.HasExited.ShouldBeFalse("the real host must be live before cancellation");
                liveVerified = true;
                output.WriteLine($"Live positive control: PID {process.Id}, inbox HTTP {(int)response.StatusCode}, HasExited={process.HasExited}");
                if (expires)
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(50));
                else
                    timeout.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
            }
            catch (OperationCanceledException error)
            {
                operationFailure = error;
                throw;
            }
            finally
            {
                await StopHost(process, stdout, stderr, operationFailure, timeout.Token);
            }
        }

        OperationCanceledException? returnedFailure = null;
        // Observe the actual exception before Shouldly translates a cancelled task.
        async Task ObserveCancellation()
        {
            try
            {
                await CancelLiveOperation();
            }
            catch (OperationCanceledException error)
            {
                returnedFailure = error;
                throw;
            }
        }

        await Should.ThrowAsync<OperationCanceledException>(ObserveCancellation);
        liveVerified.ShouldBeTrue();
        operationFailure.ShouldNotBeNull();
        returnedFailure.ShouldBeSameAs(operationFailure);
        process.HasExited.ShouldBeTrue();
        stdout.IsCanceled.ShouldBeTrue();
        stderr.IsCanceled.ShouldBeTrue();
        output.WriteLine($"Cleanup: HasExited={process.HasExited}, stdout={stdout.Status}, stderr={stderr.Status}, original failure preserved={ReferenceEquals(returnedFailure, operationFailure)}");
    }

    private static ProcessStartInfo HostStart(MailboxHttpContext context)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Weave.Mailbox.Host.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["Mailbox__DatabasePath"] = context.Data.Options.DatabasePath;
        start.Environment["Mailbox__Controls__0__MailboxId"] = "bob";
        start.Environment["Mailbox__Controls__0__Sha256"] = context.Options.Credentials.Single(c => c.MailboxId == "bob").Sha256;
        return start;
    }

    private static async Task StopHost(Process process, Task stdout, Task stderr, Exception? operationFailure,
        CancellationToken ct)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        List<Exception> failures = [];
        var unconfirmed = false;
        Exception? terminationFailure = null;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception error)
        {
            terminationFailure = error;
        }
        try
        {
            await process.WaitForExitAsync(cleanup.Token);
        }
        catch (Exception error)
        {
            if (terminationFailure is not null)
                failures.Add(terminationFailure);
            failures.Add(error);
            unconfirmed = true;
        }

        var drains = Task.WhenAll(ObserveDrain(stdout, ct), ObserveDrain(stderr, ct));
        try
        {
            foreach (var error in await drains.WaitAsync(cleanup.Token))
                if (error is not null)
                    failures.Add(error);
        }
        catch (OperationCanceledException error) when (cleanup.IsCancellationRequested)
        {
            failures.Add(error);
            unconfirmed = true;
        }

        if (failures.Count > 0)
        {
            if (operationFailure is not null)
                failures.Insert(0, operationFailure);
            throw new AggregateException(unconfirmed ? "Host process cleanup is unconfirmed." : "Host process output drain failed.", failures);
        }
    }

    private static async Task<Exception?> ObserveDrain(Task drain, CancellationToken ct)
    {
        try
        {
            await drain;
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static async Task Drain(StreamReader reader, TaskCompletionSource<Uri>? listening, CancellationToken ct)
    {
        var count = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            count += line.Length;
            count.ShouldBeLessThan(16384);
            var text = line.Trim();
            const string prefix = "Now listening on: ";
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                listening?.TrySetResult(new(text[prefix.Length..]));
        }
        listening?.TrySetException(new Xunit.Sdk.XunitException("Host process ended before it began listening."));
    }
}
