namespace Weave.Cli.Tests;

internal sealed class LocalMcpPendingHandler : HttpMessageHandler
{
    public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri.ShouldNotBeNull().AbsolutePath.ShouldBe("/api/workspaces/onboarding/tools/files/invocations");
        Requested.TrySetResult();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        return await response.Task.WaitAsync(ct);
    }
}
