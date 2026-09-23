using System.Net;
using Weave.Security.Tokens;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class UuidProposalRecoveryTests
{
    [Theory]
    [InlineData("metadata", "/proposal", HttpStatusCode.Forbidden)]
    [InlineData("other-owner", "/proposal", HttpStatusCode.NotFound)]
    [InlineData("other-workspace", "/proposal", HttpStatusCode.Forbidden)]
    [InlineData("self-review", "/approval/review", HttpStatusCode.Forbidden)]
    [InlineData("wrong-operation", "/approval/review", HttpStatusCode.Forbidden)]
    [InlineData("no-execution", "/resume", HttpStatusCode.Forbidden)]
    public async Task Fetch_UnauthorizedObjectOrOperation_DeniesBeforeLoadingCorruptBody(
        string condition, string suffix, HttpStatusCode expected)
    {
        await using var fx = new Fixture(_root, _clock);
        await fx.ConnectAsync();
        var id = await Pending(fx);
        fx.Execute("UPDATE invocation_proposals SET request_json=x'7B7D';");
        var grants = condition switch
        {
            "metadata" => new HashSet<string> { "invocation:read" },
            "self-review" => ["invocation:read", "approval:decide", "tool:files:approve:write_file"],
            "wrong-operation" => ["invocation:read", "approval:decide", "tool:files:approve:read_file"],
            _ => ["invocation:read", "invocation:proposal:read"]
        };
        var token = fx.Tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = condition == "other-workspace" ? "another" : "uuid-pilot",
            IssuedTo = condition is "other-owner" or "wrong-operation" ? "another-user" : "agent",
            Grants = grants,
            Lifetime = TimeSpan.FromMinutes(5)
        });
        using var denied = await fx.Send(suffix == "/resume" ? HttpMethod.Post : HttpMethod.Get,
            "/" + id + suffix, null, token);
        denied.StatusCode.ShouldBe(expected);
        // An authorized read proves that the corrupted body is actually detectable.
        using var control = await fx.Send(HttpMethod.Get, "/" + id + "/proposal", null, fx.Reader());
        control.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        fx.Text.ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Fact]
    public async Task Decide_ReplacementPayloadInUuidRequest_IsRejectedWithoutRecordingDecision()
    {
        await using var fx = new Fixture(_root, _clock);
        await fx.ConnectAsync();
        var id = await Pending(fx);
        var digest = await Review(fx, id);
        using var denied = await fx.Send(HttpMethod.Post, "/" + id + "/decision",
            new { decision = "approve", planDigest = digest, rawInput = "replacement" }, fx.Reviewer());
        denied.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var status = await fx.Send(HttpMethod.Get, "/" + id + "/approval", null, fx.Agent());
        (await Json(status)).GetProperty("approvalState").GetString().ShouldBe("Pending");
        fx.Text.ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Fact]
    public async Task Decide_ReviewerRevokedAfterPreview_CannotUseTheOldDigest()
    {
        await using var fx = new Fixture(_root, _clock);
        await fx.ConnectAsync();
        var id = await Pending(fx);
        var reviewer = fx.Reviewer();
        using var preview = await fx.Send(HttpMethod.Get, "/" + id + "/approval/review", null, reviewer);
        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        var digest = (await Json(preview)).GetProperty("planDigest").GetString();
        fx.Tokens.Revoke(reviewer.TokenId);
        using var denied = await fx.Send(HttpMethod.Post, "/" + id + "/decision",
            new { decision = "approve", planDigest = digest }, reviewer);
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        fx.Text.ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Fact]
    public async Task Persist_AttemptInsertFails_RollsBackNewProposalAndIntent()
    {
        await using var fx = new Fixture(_root, _clock, requireApproval: false);
        await fx.ConnectAsync();
        fx.Execute("CREATE TRIGGER reject_attempt BEFORE INSERT ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'isolated test attempt fault'); END;");
        using var denied = await fx.Send(HttpMethod.Post, "", Request(), fx.Agent());
        denied.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        fx.Text.ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocations;").ShouldBe(0);
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
        fx.Scalar("SELECT COUNT(*) FROM invocation_proposals;").ShouldBe(0);
        fx.Execute("DROP TRIGGER reject_attempt;");
        using var control = await fx.Send(HttpMethod.Post, "", Request(), fx.Agent());
        control.StatusCode.ShouldBe(HttpStatusCode.OK);
        fx.Text.ShouldBe(Proposed);
        fx.Scalar("SELECT COUNT(*) FROM invocation_proposals;").ShouldBe(1);
    }

    [Fact]
    public async Task Resume_RetainedUnconfirmedAttempt_DoesNotReplayItsAlreadyObservedEffect()
    {
        await using var fx = new Fixture(_root, _clock, requireApproval: false);
        await fx.ConnectAsync();
        var request = Request();
        using var executed = await fx.Send(HttpMethod.Post, "", request, fx.Agent());
        executed.StatusCode.ShouldBe(HttpStatusCode.OK);
        fx.Text.ShouldBe(Proposed);
        // Test-owned storage models a retained admission whose completion was not recorded.
        fx.Execute("UPDATE invocation_attempts SET outcome=0, completed_at=NULL;");
        File.WriteAllText(fx.Target, "independent later content");
        using var resumed = await fx.Send(HttpMethod.Post, "/" + request.InvocationId + "/resume", null, fx.Agent());
        resumed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var result = await Json(resumed);
        result.GetProperty("outcome").GetString().ShouldBe("OutcomeUnknown");
        result.GetProperty("isReplay").GetBoolean().ShouldBeTrue();
        fx.Text.ShouldBe("independent later content");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(1);
    }
}
