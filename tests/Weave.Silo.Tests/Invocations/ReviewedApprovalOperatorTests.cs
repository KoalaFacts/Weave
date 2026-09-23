using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class ReviewedApprovalOperatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData("approve", InvocationApprovalState.Approved)]
    [InlineData("reject", InvocationApprovalState.Rejected)]
    [InlineData("", InvocationApprovalState.Pending)]
    public async Task Review_RealPythonTerminalHelper_ConfirmsOnlyTheDisplayedPlan(string action, InvocationApprovalState expected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"weave-terminal-review-{Guid.NewGuid():N}");
        var toolRoot = Path.Combine(root, "tools");
        Directory.CreateDirectory(toolRoot);
        var target = Path.Combine(toolRoot, "note.txt");
        File.WriteAllText(target, "original");
        try
        {
            var globalSecret = "test-operator-api-" + Guid.NewGuid().ToString("N");
            await using var parent = new SiloFactory();
            await using var host = parent.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Weave:Auth:Mode", "bearer");
                builder.UseSetting("Weave:Auth:Secret", globalSecret);
                builder.UseSetting("Weave:Invocations:Http:Enabled", "true");
                builder.UseSetting("Weave:Invocations:Http:DecisionsEnabled", "true");
                builder.UseSetting("CapabilityTokens:SigningKey", "test-operator-" + Guid.NewGuid().ToString("N"));
                builder.UseSetting("CapabilityTokens:RevocationDirectory", Path.Combine(root, "revocations"));
                builder.ConfigureServices(services => services.PostConfigure<InvocationJournalOptions>(options =>
                {
                    options.DatabasePath = Path.Combine(root, "journal.db");
                    options.ApprovalRequiredGrants = ["tool:files:invoke:write_file"];
                }));
            });
            host.UseKestrel(0);
            using var client = host.CreateClient();
            var address = new UriBuilder(client.BaseAddress!) { Host = "127.0.0.1" }.Uri;
            address.IsLoopback.ShouldBeTrue();
            var tokens = host.Services.GetRequiredService<ICapabilityTokenService>();
            CapabilityToken Token(string subject, HashSet<string> grants) => tokens.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "workspace",
                IssuedTo = subject,
                Grants = grants,
                Lifetime = TimeSpan.FromMinutes(5)
            });
            var tool = host.Services.GetRequiredService<IVirtualActorProvider>()
                .GetActor<IToolActor>(VirtualActorId.From("workspace/files"));
            await tool.ConnectAsync(new ToolSpec
            {
                Name = "files",
                Type = ToolType.FileSystem,
                FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = toolRoot }
            }, Token("setup", ["tool:files:connect"]));
            var writer = Token("writer", ["tool:files:invoke:write_file", "invocation:read"]);
            var reviewer = Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
            var id = InvocationId.From(Guid.NewGuid().ToString("N"));
            var request = new ToolInvocation
            {
                InvocationId = id,
                ToolName = "files",
                Method = "write_file",
                Parameters = new() { ["path"] = "note.txt" },
                RawInput = "operator-reviewed content\n<script>data only</script>\t第二行\u001b[2J\u202e"
            };
            (await tool.InvokeAsync(request, writer)).ErrorCode.ShouldBe("approval-pending");
            var pending = (await tool.GetApprovalAsync(id, reviewer)).ShouldNotBeNull();
            var encoded = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(reviewer, JsonOptions));
            var route = $"/api/workspaces/workspace/tools/files/invocations/{id}/approval/decision";
            using (var unauthorized = new HttpRequestMessage(HttpMethod.Post, new Uri(address, route)))
            {
                unauthorized.Headers.Add("X-Weave-Capability", encoded);
                unauthorized.Content = JsonContent.Create(new { Invocation = request, PlanDigest = pending.PlanDigest, Decision = "approve" }, options: JsonOptions);
                using var rejected = await client.SendAsync(unauthorized, TestContext.Current.CancellationToken);
                rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }
            var inputPath = Path.Combine(root, "retained.json");
            File.WriteAllText(inputPath, JsonSerializer.Serialize(request, JsonOptions));
            var start = new ProcessStartInfo("python3")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(FindHelper());
            start.ArgumentList.Add("--url");
            start.ArgumentList.Add(address.ToString());
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add("workspace");
            start.ArgumentList.Add("--request");
            start.ArgumentList.Add(inputPath);
            start.Environment["WEAVE_REVIEW_CAPABILITY"] = encoded;
            start.Environment["WEAVE_OPERATOR_BEARER"] = globalSecret;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var process = new Process { StartInfo = start };
            process.Start().ShouldBeTrue();
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                var answer = (action.Length == 0 ? "" : action + " " + pending.PlanDigest) + "\n";
                await process.StandardInput.WriteAsync(answer.AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;
                var error = await stderr;
                process.ExitCode.ShouldBe(0, "The operator example must exit normally.");
                error.ShouldBeEmpty();
                output.ShouldContain(pending.PlanDigest);
                output.ShouldContain("operator-reviewed content");
                output.ShouldContain("\\u001b");
                output.ShouldContain("\\u202e");
                output.ShouldNotContain("\u001b");
                output.ShouldNotContain(encoded);
                output.ShouldNotContain(globalSecret);
                output.ShouldContain(action.Length == 0 ? "No decision sent." : expected + ". No tool execution");
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
            (await tool.GetApprovalAsync(id, reviewer)).ShouldNotBeNull().State.ShouldBe(expected);
            (await tool.GetInvocationAsync(id, writer)).ShouldBeNull();
            File.ReadAllText(target).ShouldBe("original");
            if (expected == InvocationApprovalState.Approved)
            {
                (await tool.InvokeAsync(request, writer)).Success.ShouldBeTrue();
                File.ReadAllText(target).ShouldBe(request.RawInput);
                File.WriteAllText(target, "later external change");
                (await tool.InvokeAsync(request, writer)).IsReplay.ShouldBeTrue();
                File.ReadAllText(target).ShouldBe("later external change");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindHelper()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Weave.slnx")))
                return Path.Combine(directory.FullName, "examples", "governed-tools", "review.py");
        throw new DirectoryNotFoundException("The checked-out operator example is required.");
    }
}
