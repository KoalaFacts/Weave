using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Fact]
    public async Task Post_ExternalPythonProcessOnRealTcp_ReadsButCannotWrite()
    {
        var observer = new PythonRoutingObserver();
        await using var fx = new Fixture(useKestrel: true, requestObserver: observer);
        await fx.ConnectAsync();
        // Kestrel and Grain setup precede the first-request initialization of HTTP routing.
        using var readiness = await fx.Client.GetAsync("/health", TestContext.Current.CancellationToken);
        readiness.StatusCode.ShouldBe(HttpStatusCode.OK);
        await observer.RoutingReady.Task.WaitAsync(fx.Client.Timeout, TestContext.Current.CancellationToken);
        var address = new Uri(fx.Client.BaseAddress!, fx.Route);
        address.IsLoopback.ShouldBeTrue();
        var input = JsonSerializer.Serialize(new
        {
            url = address.ToString(),
            capability = Encode(fx.Token(grants: ["tool:files:invoke:read_file"])),
            read = Request("read_file"),
            write = Request()
        }, JsonOptions);
        const string script = """
            import json, sys, urllib.request, urllib.error
            class NoRedirect(urllib.request.HTTPRedirectHandler):
                def redirect_request(self, req, fp, code, msg, headers, newurl):
                    return None
            data = json.load(sys.stdin)
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
            def invoke(payload):
                request = urllib.request.Request(data['url'], json.dumps(payload).encode('utf-8'),
                    {'Content-Type': 'application/json', 'X-Weave-Capability': data['capability']}, method='POST')
                try:
                    with opener.open(request, timeout=10) as response:
                        return {'status': response.status, 'body': json.loads(response.read(1048576))}
                except urllib.error.HTTPError as error:
                    return {'status': error.code, 'body': json.loads(error.read(1048576))}
            print(json.dumps({'read': invoke(data['read']), 'write': invoke(data['write'])}))
            """;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var execution = await RunPythonAsync(script, input, timeout.Token);
        execution.Error.ShouldBeEmpty();
        execution.ExitCode.ShouldBe(0, "The local Python HTTP client must complete normally.");
        using var result = JsonDocument.Parse(execution.Output);
        result.RootElement.GetProperty("read").GetProperty("status").GetInt32().ShouldBe(200);
        result.RootElement.GetProperty("read").GetProperty("body").GetProperty("output").GetString().ShouldBe("original");
        result.RootElement.GetProperty("write").GetProperty("status").GetInt32().ShouldBe(403);
        File.ReadAllText(fx.Target).ShouldBe("original");
        observer.ReadinessBeforeInvocations.ToArray().ShouldBe([true, true],
            "Both Python requests must follow a completed, matched HTTP health request.");
        execution.ReaderWasThreadPool.ShouldBe([false, false],
            "Blocking process pipe reads must leave the host's worker pool available.");
    }

    private sealed class PythonRoutingObserver : IStartupFilter
    {
        public TaskCompletionSource RoutingReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<bool> ReadinessBeforeInvocations { get; } = new();

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                if (context.Request.Method == HttpMethods.Post)
                    ReadinessBeforeInvocations.Enqueue(RoutingReady.Task.IsCompletedSuccessfully);
                await proceed(context);
                if (context.Request.Path == "/health" && context.GetEndpoint() is not null
                    && context.Response.StatusCode == StatusCodes.Status200OK)
                    RoutingReady.TrySetResult();
            });
            next(app);
        };
    }
}
