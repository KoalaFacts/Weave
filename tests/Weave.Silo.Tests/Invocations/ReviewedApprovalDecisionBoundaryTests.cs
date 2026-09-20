using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class ReviewedApprovalDecisionTests
{
    [Theory]
    [InlineData("decision")]
    [InlineData("Decision")]
    public async Task Decide_DuplicateDecisionFields_RejectsAmbiguousPayload(string duplicateName)
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var original = await fx.CurrentAsync();
        var payload = JsonSerializer.Serialize(new { Invocation = fx.Request, PlanDigest = original.PlanDigest }, JsonOptions);
        payload = payload[..^1] + ",\"decision\":\"reject\",\"" + duplicateName + "\":\"approve\"}";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await fx.SendAsync(fx.Route + "/approval/decision", body, fx.Reviewer());
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fx.CurrentAsync()).ShouldBe(original);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Decide_UnknownDecisionFields_DoesNotSilentlyAcceptAdditionalInstructions()
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var original = await fx.CurrentAsync();
        using var response = await fx.SendAsync(fx.Route + "/approval/decision", new
        {
            Invocation = fx.Request,
            PlanDigest = original.PlanDigest,
            Decision = "approve",
            ExecuteImmediately = true
        }, fx.Reviewer());
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fx.CurrentAsync()).ShouldBe(original);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Approve")]
    [InlineData("cancel")]
    [InlineData("unknown")]
    public async Task Decide_InvalidAction_HasNoApprovedDefault(string decision)
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var original = await fx.CurrentAsync();
        using var response = await fx.DecideAsync(decision, original.PlanDigest);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fx.CurrentAsync()).ShouldBe(original);
    }

    [Fact]
    public async Task Decide_RepeatedConfirmation_DoesNotReplaceDecisionOrDispatch()
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var pending = await fx.CurrentAsync();
        using var first = await fx.DecideAsync("approve", pending.PlanDigest);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var decided = await fx.CurrentAsync();
        using var repeated = await fx.DecideAsync("approve", pending.PlanDigest);
        repeated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(repeated)).GetProperty("errorCode").GetString().ShouldBe("approval-not-pending");
        (await fx.CurrentAsync()).ShouldBe(decided);
        CountDecisions(fx).ShouldBe(1L);
        (await fx.Tool.GetInvocationAsync(fx.Request.InvocationId!.Value, fx.Writer())).ShouldBeNull();
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Decide_ConcurrentOppositeConfirmations_OnlyOneDecisionCommits()
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var pending = await fx.CurrentAsync();
        var responses = await Task.WhenAll(fx.DecideAsync("approve", pending.PlanDigest),
            fx.DecideAsync("reject", pending.PlanDigest));
        using var first = responses[0];
        using var second = responses[1];
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        CountDecisions(fx).ShouldBe(1L);
        (await fx.Tool.GetInvocationAsync(fx.Request.InvocationId!.Value, fx.Writer())).ShouldBeNull();
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Decide_HistoryInsertFails_RollsBackAndReturnsNoPrivateDatabaseDetail()
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var pending = await fx.CurrentAsync();
        using (var connection = OpenJournal(fx))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER no_operator_decision BEFORE INSERT ON invocation_approval_decisions BEGIN SELECT RAISE(ABORT, 'private database detail'); END;";
            command.ExecuteNonQuery();
        }
        using var response = await fx.DecideAsync("approve", pending.PlanDigest);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("private database detail");
        (await fx.CurrentAsync()).ShouldBe(pending);
        CountDecisions(fx).ShouldBe(0L);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("duplicate")]
    public async Task Decide_InvalidCredential_DoesNotChangeApproval(string condition)
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var pending = await fx.CurrentAsync();
        var token = condition == "expired" ? fx.Tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = fx.Workspace,
            IssuedTo = "operator",
            Grants = ["invocation:read", "approval:decide", "tool:files:approve:write_file"],
            Lifetime = TimeSpan.FromMinutes(-1)
        }) : fx.Reviewer();
        if (condition == "tampered")
            token = token with { IssuedTo = "forged-operator" };
        if (condition == "revoked")
            fx.Tokens.Revoke(token.TokenId);
        using var message = new HttpRequestMessage(HttpMethod.Post, fx.Route + "/approval/decision");
        message.Content = JsonContent.Create(new { Invocation = fx.Request, PlanDigest = pending.PlanDigest, Decision = "approve" }, options: JsonOptions);
        if (condition != "missing")
        {
            var encoded = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions));
            message.Headers.TryAddWithoutValidation("X-Weave-Capability", encoded);
            if (condition == "duplicate")
                message.Headers.TryAddWithoutValidation("X-Weave-Capability", encoded);
        }
        using var response = await fx.Client.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fx.CurrentAsync()).ShouldBe(pending);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain(token.Signature);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decide_OversizedBody_IsBoundedEvenWithoutContentLength(bool knownLength)
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var pending = await fx.CurrentAsync();
        var payload = Encoding.UTF8.GetBytes(new string(' ', 1_048_577));
        using HttpContent content = knownLength ? new ByteArrayContent(payload) : new StreamingContent(payload);
        content.Headers.ContentType = new("application/json");
        using var response = await fx.SendAsync(fx.Route + "/approval/decision", content, fx.Reviewer());
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await fx.CurrentAsync()).ShouldBe(pending);
    }

    private static SqliteConnection OpenJournal(Fixture fx)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fx.DatabasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long CountDecisions(Fixture fx)
    {
        using var connection = OpenJournal(fx);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM invocation_approval_decisions;";
        return (long)command.ExecuteScalar()!;
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, CancellationToken.None).AsTask();
    }
}
