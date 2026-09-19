using Weave.Invocations;

namespace Weave.Silo.Tests.Invocations;

public sealed class FileWriteApprovalFlowTests
{
    [Fact]
    public async Task ResumeApprovedAsync_SeparateReviewer_WritesOnlyAfterExplicitResume()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var request = FileWriteApprovalScenario.Request();
        var approval = await fx.ProposeAsync(request);
        approval.Plan.Content.ShouldBe(request.RawInput);
        approval.Plan.Root.ShouldBe(fx.DataRoot);
        approval.Plan.FileName.ShouldBe("note.txt");
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
        var decision = await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer);
        decision.Applied.ShouldBeTrue();
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        var resubmitted = await fx.Tool.InvokeAsync(request, fx.Writer);
        resubmitted.ApprovalState.ShouldBe(ApprovalState.Approved);
        File.ReadAllText(fx.FilePath).ShouldBe("original");

        var result = await fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer);

        result.Success.ShouldBeTrue(result.Error);
        result.Outcome.ShouldBe(InvocationOutcome.Succeeded);
        result.OutcomeRecorded.ShouldBeTrue();
        result.AttemptId.ShouldNotBeNull();
        File.ReadAllText(fx.FilePath).ShouldBe(request.RawInput);
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(1);
        (await fx.Tool.GetApprovalAsync(approval.Plan.InvocationId, fx.Reviewer)).ShouldNotBeNull().State.ShouldBe(ApprovalState.Consumed);
        File.WriteAllText(fx.FilePath, "changed after the first execution");
        var replay = await fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer);
        replay.IsReplay.ShouldBeTrue();
        File.ReadAllText(fx.FilePath).ShouldBe("changed after the first execution");
    }

    [Fact]
    public async Task ResumeApprovedAsync_HostRestart_PreservesPendingPlanDecisionAndOutcome()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var request = FileWriteApprovalScenario.Request();
        var approval = await fx.ProposeAsync(request);
        await fx.RestartAsync();
        var restored = (await fx.Tool.GetApprovalAsync(approval.Plan.InvocationId, fx.Reviewer)).ShouldNotBeNull();
        restored.ShouldBe(approval);
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        (await fx.Tool.DecideApprovalAsync(restored.Plan.InvocationId, restored.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeTrue();
        await fx.RestartAsync();
        (await fx.Tool.ResumeApprovedAsync(restored.Plan.InvocationId, fx.Writer)).Success.ShouldBeTrue();
        File.ReadAllText(fx.FilePath).ShouldBe(request.RawInput);
        await fx.RestartAsync();
        var record = (await fx.Tool.GetInvocationAsync(restored.Plan.InvocationId, fx.Writer)).ShouldNotBeNull();
        record.Attempt.Outcome.ShouldBe(InvocationOutcome.Succeeded);
    }

    [Fact]
    public async Task ResumeApprovedAsync_ConcurrentRequests_ClaimsOneAttempt()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeTrue();
        var results = await Task.WhenAll(fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer),
            fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer));
        results.Count(r => r.Success && !r.IsReplay).ShouldBe(1);
        results.Count(r => r.IsReplay).ShouldBe(1);
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(1);
    }

    [Fact]
    public async Task ProposeApproval_ProtectedBody_IsNotPlaintextInJournal()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var request = FileWriteApprovalScenario.Request();
        await fx.ProposeAsync(request);
        fx.Sql("SELECT COUNT(*) FROM invocation_approvals WHERE instr(protected_plan, 'CONTENT-MARKER') > 0;").ShouldBe(0);
        fx.Sql("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(1);
        foreach (var file in Directory.GetFiles(fx.Root, "journal.db*", SearchOption.TopDirectoryOnly))
            System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)).ShouldNotContain("CONTENT-MARKER");
    }

    [Fact]
    public async Task ProposeApproval_JournalWriteFails_NoAttemptOrEffect()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        fx.Sql("CREATE TRIGGER refuse_approval BEFORE INSERT ON invocation_approvals BEGIN SELECT RAISE(ABORT, 'private detail'); END;");
        var result = await fx.Tool.InvokeAsync(FileWriteApprovalScenario.Request(), fx.Writer);
        result.ErrorCode.ShouldBe("approval-recording-failed");
        result.Error.ShouldNotBeNull().ShouldNotContain("private detail");
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        fx.Sql("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(0);
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Fact]
    public async Task ResumeApprovedAsync_AttemptInsertFails_RollsBackApprovalConsumption()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeTrue();
        fx.Sql("CREATE TRIGGER refuse_attempt BEFORE INSERT ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'private detail'); END;");
        var result = await fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer);
        result.ErrorCode.ShouldBe("journal-write-failed");
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        fx.Sql("SELECT COUNT(*) FROM invocations;").ShouldBe(0);
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
        (await fx.Tool.GetApprovalAsync(approval.Plan.InvocationId, fx.Reviewer)).ShouldNotBeNull().State.ShouldBe(ApprovalState.Approved);
    }
}
