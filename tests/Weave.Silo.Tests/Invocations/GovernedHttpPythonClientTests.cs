using System.Diagnostics;
using System.Text.Json;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Fact]
    public async Task Post_ExternalPythonProcessOnRealTcp_ReadsButCannotWrite()
    {
        await using var fx = new Fixture(useKestrel: true);
        await fx.ConnectAsync();
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
        var start = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using var process = new Process { StartInfo = start };
        process.Start().ShouldBeTrue();
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            process.ExitCode.ShouldBe(0, "The local Python HTTP client must complete normally.");
            error.ShouldBeEmpty();
            using var result = JsonDocument.Parse(output);
            result.RootElement.GetProperty("read").GetProperty("status").GetInt32().ShouldBe(200);
            result.RootElement.GetProperty("read").GetProperty("body").GetProperty("output").GetString().ShouldBe("original");
            result.RootElement.GetProperty("write").GetProperty("status").GetInt32().ShouldBe(403);
            File.ReadAllText(fx.Target).ShouldBe("original");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }
}
