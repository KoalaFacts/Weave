using System.Security.Cryptography;
using Weave.Invocations;

namespace Weave.Silo.Tests.Invocations;

public sealed class FileWriteApprovalSafetyTests
{
    [Fact]
    public async Task InvokeAsync_NoExecutionGrant_CannotEnterApprovalAsAnElevationPath()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Tool.InvokeAsync(
            FileWriteApprovalScenario.Request(), fx.Mint("unprivileged", "approval:read")));
        fx.Sql("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(0);
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
        File.ReadAllText(fx.FilePath).ShouldBe("original");
    }

    [Fact]
    public async Task ResumeApprovedAsync_AnotherWriter_CannotUseTheRequestersApproval()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeTrue();
        var result = await fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId,
            fx.Mint("another-writer", "tool:files:invoke:write_file"));
        result.ErrorCode.ShouldBe("approval-not-found");
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        (await fx.Tool.GetApprovalAsync(approval.Plan.InvocationId, fx.Writer)).ShouldNotBeNull().State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public async Task ResumeApprovedAsync_DestinationReplacedWithLink_DoesNotWriteLinkTarget()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeTrue();
        var outside = Path.Combine(fx.Root, "outside.txt");
        File.WriteAllText(outside, "outside unchanged");
        File.Delete(fx.FilePath);
        File.CreateSymbolicLink(fx.FilePath, outside);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer));
        File.ReadAllText(outside).ShouldBe("outside unchanged");
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Fact]
    public async Task ResumeApprovedAsync_ProtectedPlanChanged_CannotExecuteTamperedContent()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeTrue();
        fx.Sql("UPDATE invocation_approvals SET protected_plan='invalid-ciphertext';");
        await Should.ThrowAsync<CryptographicException>(() => fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer));
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
        File.ReadAllText(fx.FilePath).ShouldBe("original");
    }

    [Fact]
    public async Task InvokeAsync_ReadOnlyOperation_StillExecutesWithExactReadGrant()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var request = FileWriteApprovalScenario.Request() with { Method = "read_file", RawInput = null };
        var result = await fx.Tool.InvokeAsync(request, fx.Mint("reader", "tool:files:invoke:read_file"));
        result.Success.ShouldBeTrue(result.Error);
        result.Output.ShouldBe("original");
        result.OutcomeRecorded.ShouldBeTrue();
        fx.Sql("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(0);
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(1);
    }

    [Fact]
    public async Task InvokeAsync_ReviewPayloadTooLarge_DoesNotPersistOrExecute()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var request = FileWriteApprovalScenario.Request() with { RawInput = new string('x', 16_385) };
        var result = await fx.Tool.InvokeAsync(request, fx.Writer);
        result.ErrorCode.ShouldBe("approval-operation-unsupported");
        fx.Sql("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(0);
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
        File.ReadAllText(fx.FilePath).ShouldBe("original");
    }
}
