using System.Net;
using Microsoft.Data.Sqlite;
using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class SingleHostStorageRecoveryTests
{
    [Theory]
    [InlineData("journal")]
    [InlineData("revocations")]
    [InlineData("both")]
    public async Task Start_RequiredStoreIsMissing_DoesNotReinitializeLostHistory(string missing)
    {
        await RunHostAsync(false, false, _ => Task.CompletedTask);
        var journal = Path.Combine(_root, "journal-retained.db");
        var revocations = Path.Combine(_root, "revocations-retained");
        if (missing is "journal" or "both")
            File.Move(Database, journal);
        if (missing is "revocations" or "both")
            Directory.Move(Revocations, revocations);

        var error = await Record.ExceptionAsync(() => RunHostAsync(true, false, _ => Task.CompletedTask));

        error.ShouldNotBeNull();
        if (missing is "journal" or "both")
            File.Exists(Database).ShouldBeFalse();
        if (missing is "revocations" or "both")
            Directory.Exists(Revocations).ShouldBeFalse();
        File.ReadAllText(Target).ShouldBe("original");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unrelated")]
    [InlineData("missing-approvals")]
    [InlineData("directory")]
    [InlineData("corrupt")]
    public async Task Start_RequiredJournalIsNotUsable_FailsWithoutRepairingOrReplacingIt(string replacement)
    {
        await RunHostAsync(false, false, _ => Task.CompletedTask);
        if (replacement == "missing-approvals")
            Sql("DROP TABLE invocation_approval_decisions; DROP TABLE invocation_approvals;");
        else
        {
            File.Move(Database, Path.Combine(_root, "journal-retained.db"));
            if (replacement == "directory")
                Directory.CreateDirectory(Database);
            else if (replacement == "corrupt")
                File.WriteAllText(Database, "not a SQLite database");
            else
            {
                File.WriteAllBytes(Database, []);
                if (replacement == "unrelated")
                    Sql("CREATE TABLE unrelated(value TEXT);");
            }
        }
        var before = File.Exists(Database) ? File.ReadAllBytes(Database) : null;

        var error = await Record.ExceptionAsync(() => RunHostAsync(true, false, _ => Task.CompletedTask));

        error.ShouldNotBeNull();
        if (before is not null)
            File.ReadAllBytes(Database).ShouldBe(before);
        File.ReadAllText(Target).ShouldBe("original");
    }

    [Fact]
    public async Task Restart_RetainedStores_PreservesApprovalRevocationAndAgentOnlyBoundary()
    {
        var request = Request();
        CapabilityToken writer = null!;
        CapabilityToken revoked = null!;
        CapabilityToken reviewer = null!;
        string digest = null!;
        await RunHostAsync(false, true, async host =>
        {
            await host.ConnectAsync();
            writer = host.Token("writer");
            revoked = host.Token("writer");
            reviewer = host.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
            host.Tokens.Revoke(revoked.TokenId);
            using var pending = await host.SendAsync(request, writer);
            pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            digest = (await host.Tool.GetApprovalAsync(request.InvocationId!.Value, writer)).ShouldNotBeNull().PlanDigest;
            File.ReadAllText(Target).ShouldBe("original");
        });

        await RunHostAsync(true, true, async host =>
        {
            (await host.Tool.GetHandleAsync()).ShouldBeNull();
            host.Tokens.Validate(writer).ShouldBeTrue();
            host.Tokens.Validate(revoked).ShouldBeFalse();
            var approval = (await host.Tool.GetApprovalAsync(request.InvocationId!.Value, reviewer)).ShouldNotBeNull();
            approval.State.ShouldBe(InvocationApprovalState.Pending);
            approval.PlanDigest.ShouldBe(digest);
            (await host.Tool.GetInvocationAsync(request.InvocationId.Value, writer)).ShouldBeNull();
            File.ReadAllText(Target).ShouldBe("original");
            await host.ConnectAsync();
            using var denied = await host.SendAsync(request, revoked);
            denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            // Trusted in-process operator, same Host/journal; never an Agent HTTP management route.
            (await host.Tool.DecideApprovalAsync(request.InvocationId.Value, digest,
                InvocationApprovalDecision.Approve, reviewer)).Succeeded.ShouldBeTrue();
            File.ReadAllText(Target).ShouldBe("original");
            using var done = await host.SendAsync(request, writer);
            done.StatusCode.ShouldBe(HttpStatusCode.OK);
            File.ReadAllText(Target).ShouldBe("retained request body");
            using var management = await host.Client.GetAsync("/api/workspaces", TestContext.Current.CancellationToken);
            management.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            using var decision = await host.Client.PostAsync(host.Route + "/" + request.InvocationId + "/approval/decision",
                null, TestContext.Current.CancellationToken);
            decision.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        });

        File.WriteAllText(Target, "later external edit");
        await RunHostAsync(true, true, async host =>
        {
            await host.ConnectAsync();
            using var duplicate = await host.SendAsync(request, writer);
            duplicate.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await JsonAsync(duplicate)).GetProperty("isReplay").GetBoolean().ShouldBeTrue();
            (await host.Tool.GetApprovalAsync(request.InvocationId!.Value, reviewer)).ShouldNotBeNull()
                .State.ShouldBe(InvocationApprovalState.Consumed);
            File.ReadAllText(Target).ShouldBe("later external edit");
        });
    }

    [Fact]
    public async Task Restart_AfterUnconfirmedCompletion_RetainsUnknownAndNeverRepeatsTheEffect()
    {
        var request = Request();
        CapabilityToken writer = null!;
        await RunHostAsync(false, false, async host =>
        {
            await host.ConnectAsync();
            writer = host.Token("writer");
            Sql("CREATE TRIGGER fail_completion BEFORE UPDATE ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'test completion fault'); END;");
            using var unknown = await host.SendAsync(request, writer);
            unknown.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await JsonAsync(unknown)).GetProperty("outcome").GetString().ShouldBe("OutcomeUnknown");
            File.ReadAllText(Target).ShouldBe("retained request body");
        });
        Sql("DROP TRIGGER fail_completion;");
        File.WriteAllText(Target, "later external edit");
        await RunHostAsync(true, false, async host =>
        {
            var stored = (await host.Tool.GetInvocationAsync(request.InvocationId!.Value, writer)).ShouldNotBeNull();
            stored.Attempt.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
            stored.Attempt.CompletedAt.ShouldBeNull();
            await host.ConnectAsync();
            using var repeated = await host.SendAsync(request, writer);
            repeated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await JsonAsync(repeated)).GetProperty("outcome").GetString().ShouldBe("OutcomeUnknown");
            File.ReadAllText(Target).ShouldBe("later external edit");
        });
    }
}
