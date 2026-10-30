using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Weave.Silo.Tests.Dashboard;

internal sealed class DashboardReviewBrowserApi : IAsyncDisposable
{
    public const string ReviewPath = "/api/workspaces/release-lab/tools/files/invocations/abcdef0123456789abcdef0123456789/approval/review";
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _hold;
    private readonly bool _deny;

    public DashboardReviewBrowserApi(string root, bool hold = false, bool deny = false)
    {
        _hold = hold;
        _deny = deny;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            EnvironmentName = "Development"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, 0);
            server.Limits.MaxRequestBodySize = 2_097_152;
        });
        _app = builder.Build();
        _app.Run(HandleAsync);
    }

    public Uri Address => new(_app.Urls.Single());
    public ConcurrentQueue<ReviewRequest> Requests { get; } = new();
    public TaskCompletionSource<ReviewRequest> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task StartAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await _app.StartAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        var request = new ReviewRequest(context.Request.Method, context.Request.Path.Value.ShouldNotBeNull(), body,
            context.Request.Headers["X-Weave-Capability"].ToString(), context.Request.Headers.Authorization.ToString());
        Requests.Enqueue(request);
        using var cancelled = context.RequestAborted.Register(() => Cancelled.TrySetResult());
        Entered.TrySetResult(request);
        try
        {
            if (request.Method != "POST" || request.Path != ReviewPath)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (_hold)
                await Release.Task.WaitAsync(_stop.Token);
            if (_deny)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("private-upstream-denial-marker", context.RequestAborted);
                return;
            }
            var original = JsonNode.Parse(body).ShouldBeOfType<JsonObject>();
            var snapshot = new JsonObject
            {
                ["invocationId"] = original["invocationId"]?.DeepClone(),
                ["workspaceId"] = "release-lab",
                ["subject"] = "original-release-agent",
                ["toolName"] = "files",
                ["operation"] = original["method"]?.DeepClone(),
                ["parameters"] = original["parameters"]?.DeepClone(),
                ["rawInput"] = original["rawInput"]?.DeepClone(),
                ["targetDescription"] = "FileSystem controlled release root",
                ["planDigest"] = "browser-review-plan-marker",
                ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(snapshot.ToJsonString(), context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || _stop.IsCancellationRequested)
        {
            // Clear/circuit shutdown closes the real upstream request; the test observes Cancelled.
        }
        finally
        {
            Completed.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        Release.TrySetResult();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(deadline.Token);
        }
        finally
        {
            await _app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }

    internal sealed record ReviewRequest(string Method, string Path, string Body, string Capability, string Authorization);
}
