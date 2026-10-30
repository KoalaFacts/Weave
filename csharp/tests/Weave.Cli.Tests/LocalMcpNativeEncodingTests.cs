using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Weave.Cli.Tests;

public sealed class LocalMcpNativeEncodingTests
{
    [Fact]
    public async Task Mcp_RawUtf8Pipe_PreservesChinesePathAndProposalWithoutConsoleCodePage()
    {
        const string id = "2ad88c22863d45da931d91cd1ca26a73";
        const string path = "审阅清单.md";
        const string content = "会议审批：陈晨；日期未确认。\n第二行 😀";
        Guid.TryParseExact(id, "N", out _).ShouldBeTrue();
        using var files = new LocalTestDirectory();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = ReceiveProposalAsync(listener, id, deadline.Token);
        var executable = Path.Join(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "weave.exe" : "weave");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("WEAVE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CapabilityTokens", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["WEAVE_AGENT_CAPABILITY"] = "fixture-agent";
        foreach (var argument in new[] { "local", "mcp", "--url", $"http://127.0.0.1:{port}", "--workspace", "onboarding", "--receipts", files.Private })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var stdout = Task.Factory.StartNew(child.StandardOutput.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var stderr = Task.Factory.StartNew(child.StandardError.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            var call = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "submit_write",
                    ["arguments"] = new JsonObject
                    { ["invocation_id"] = id, ["path"] = path, ["content"] = content }
                }
            };
            var rawCall = call.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            rawCall.ShouldContain("会议审批");
            var input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n"
                + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" + rawCall + "\n";
            await child.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(input), deadline.Token);
            child.StandardInput.Close();
            await child.WaitForExitAsync(deadline.Token);
            child.ExitCode.ShouldBe(0, await stderr.WaitAsync(deadline.Token));
            var output = await stdout.WaitAsync(deadline.Token);
            var result = JsonNode.Parse(output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1])!;
            result["result"]!["isError"]!.GetValue<bool>().ShouldBeFalse("Native MCP output: " + output);
            var proposal = await received;
            proposal["parameters"]!["path"]!.GetValue<string>().ShouldBe(path);
            proposal["rawInput"]!.GetValue<string>().ShouldBe(content);
            output.ShouldNotStartWith("\ufeff");
            var business = JsonNode.Parse(result["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
            business["http_status"]!.GetValue<int>().ShouldBe(202);
            business["result"]!["approvalState"]!.GetValue<string>().ShouldBe("Pending");
        }
        finally
        {
            deadline.Cancel();
            listener.Stop();
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            try
            { await received; }
            catch (OperationCanceledException canceled) when (canceled.CancellationToken == deadline.Token)
            {
                deadline.IsCancellationRequested.ShouldBeTrue();
            }
        }
    }

    private static async Task<JsonObject> ReceiveProposalAsync(TcpListener listener, string id, CancellationToken ct)
    {
        var header = new StringBuilder();
        var next = new byte[1];
        for (var index = 0; index < 3; index++)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            header.Clear();
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(next, ct);
                header.Append((char)next[0]);
                header.Length.ShouldBeLessThan(8192);
            }
            var lengthHeader = header.ToString().Split("\r\n").SingleOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthHeader is null ? 0 : int.Parse(lengthHeader.AsSpan("Content-Length:".Length), System.Globalization.CultureInfo.InvariantCulture);
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, ct);
            var body = length == 0 ? null : JsonNode.Parse(bytes)!.AsObject();
            var submitted = header.ToString().StartsWith("POST ", StringComparison.Ordinal);
            var response = submitted ? new JsonObject
            {
                ["invocationId"] = id,
                ["success"] = false,
                ["outcomeRecorded"] = false,
                ["outcome"] = "NotDispatched",
                ["approvalState"] = "Pending"
            } : new JsonObject
            {
                ["errorCode"] = header.ToString().StartsWith("GET /api/workspaces/onboarding/tools/files/invocations/" + id + "/approval ", StringComparison.Ordinal)
                ? "approval-not-found" : "invocation-not-found"
            };
            var data = Encoding.UTF8.GetBytes(response.ToJsonString());
            var status = submitted ? "202 Accepted" : "404 Not Found";
            var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeader, ct);
            await stream.WriteAsync(data, ct);
            if (submitted)
                return body!;
        }
        throw new InvalidOperationException("The native MCP client did not submit the proposal.");
    }
}
