using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class HostProcessRecoveryTests
{
    private const string ChildRootVariable = "WEAVE_TEST_HOST_PROCESS_ROOT";
    private const string Route = "/api/workspaces/lifetime/tools/files/invocations";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Restart_GracefulAndKilledProcesses_PreservesOutcomesAndRejectsStaleAuthority()
    {
        var childRoot = Environment.GetEnvironmentVariable(ChildRootVariable);
        if (childRoot is not null)
        {
            await RunChildAsync(childRoot);
            return;
        }
        foreach (var interrupted in new[] { false, true })
            await VerifyRestartAsync(interrupted);
    }

    private static async Task VerifyRestartAsync(bool interrupted)
    {
        var root = Path.Combine(Path.GetTempPath(), "weave-process-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        var target = Path.Combine(root, "tools", "note.txt");
        File.WriteAllText(target, "original");
        var key = "test-process-only-" + Guid.NewGuid().ToString("N");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        var tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
        {
            SigningKey = key,
            RevocationDirectory = Path.Combine(root, "state", "revocations")
        }), clock);
        CapabilityToken Mint(string subject, int minutes) => tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "lifetime",
            IssuedTo = subject,
            Grants = ["tool:files:invoke:write_file", "invocation:read"],
            Lifetime = TimeSpan.FromMinutes(minutes)
        });
        var shortLived = Mint("agent", 1);
        var writer = Mint("agent", 30);
        var foreign = Mint("other-agent", 30);
        var revoked = Mint("agent", 30);
        tokens.Revoke(revoked.TokenId);
        var invocation = new ToolInvocation
        {
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
            ToolName = "files",
            Method = "write_file",
            Parameters = new() { ["path"] = "note.txt" },
            RawInput = "a confirmed file effect"
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await using (var first = await HostChild.StartAsync(root, new WorkerSettings(key, clock.GetUtcNow(), false, interrupted), deadline.Token))
            {
                var sending = SendAsync(first.Client, HttpMethod.Post, Route, invocation, shortLived, deadline.Token);
                if (interrupted)
                {
                    try
                    {
                        await first.WaitForFileAsync(Path.Combine(root, "effect-ready"), deadline.Token);
                        File.ReadAllText(target).ShouldBe(invocation.RawInput);
                        sending.IsCompleted.ShouldBeFalse();
                        await first.KillAsync(deadline.Token);
                        var error = await Record.ExceptionAsync(async () => { using var response = await sending; });
                        error.ShouldNotBeNull();
                    }
                    finally
                    {
                        // Always observe the pending HTTP task before disposing the parent fixture.
                        first.Client.CancelPendingRequests();
                        try { using var response = await sending; }
                        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
                    }
                }
                else
                {
                    using var response = await sending;
                    response.StatusCode.ShouldBe(HttpStatusCode.OK);
                    (await BodyAsync(response, deadline.Token)).GetProperty("outcome").GetString().ShouldBe("Succeeded");
                    File.ReadAllText(target).ShouldBe(invocation.RawInput);
                    await first.StopAsync(deadline.Token);
                }
            }

            File.WriteAllText(target, "external change after stop");
            clock.Advance(TimeSpan.FromMinutes(2));
            await using var second = await HostChild.StartAsync(root, new WorkerSettings(key, clock.GetUtcNow(), true, interrupted), deadline.Token);
            using var stale = await SendAsync(second.Client, HttpMethod.Post, Route, invocation, shortLived, deadline.Token);
            stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            using var revokedReply = await SendAsync(second.Client, HttpMethod.Post, Route, invocation, revoked, deadline.Token);
            revokedReply.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var queryRoute = Route + "/" + invocation.InvocationId;
            using var hidden = await SendAsync(second.Client, HttpMethod.Get, queryRoute, null, foreign, deadline.Token);
            hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            using var observed = await SendAsync(second.Client, HttpMethod.Get, queryRoute, null, writer, deadline.Token);
            observed.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await BodyAsync(observed, deadline.Token)).GetProperty("outcome").GetString()
                .ShouldBe(interrupted ? "OutcomeUnknown" : "Succeeded");
            using var duplicate = await SendAsync(second.Client, HttpMethod.Post, Route, invocation, writer, deadline.Token);
            duplicate.StatusCode.ShouldBe(interrupted ? HttpStatusCode.Conflict : HttpStatusCode.OK);
            if (!interrupted)
                (await BodyAsync(duplicate, deadline.Token)).GetProperty("isReplay").GetBoolean().ShouldBeTrue();
            File.ReadAllText(target).ShouldBe("external change after stop");
            File.ReadAllLines(Path.Combine(root, "dispatch-count")).Length.ShouldBe(1);
            using var management = await second.Client.GetAsync("/api/workspaces", deadline.Token);
            management.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            using var decision = await second.Client.PostAsync(queryRoute + "/approval/decision", null, deadline.Token);
            decision.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            await second.StopAsync(deadline.Token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route,
        ToolInvocation? invocation, CapabilityToken token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, route);
        request.Headers.Add("X-Weave-Capability", WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions)));
        if (invocation is not null)
            request.Content = JsonContent.Create(invocation, options: JsonOptions);
        return await client.SendAsync(request, ct);
    }

    private static Task<JsonElement> BodyAsync(HttpResponseMessage response, CancellationToken ct) =>
        response.Content.ReadFromJsonAsync<JsonElement>(ct);

    private sealed record WorkerSettings(string SigningKey, DateTimeOffset Now, bool Recovery, bool HoldCompletion);
}
