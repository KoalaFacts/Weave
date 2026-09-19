using Weave.Invocations;

namespace Weave.Silo.Tests.Invocations;

public sealed class FileWriteApprovalPlanTests
{
    [Theory]
    [InlineData("content")]
    [InlineData("filename")]
    public async Task InvokeAsync_ChangedRequestUnderSameId_CannotReplacePlan(string change)
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var request = FileWriteApprovalScenario.Request();
        var approval = await fx.ProposeAsync(request);
        var changed = change == "content" ? request with { RawInput = "replacement" }
            : request with { Parameters = new() { ["path"] = "other.txt" } };
        var conflict = await fx.Tool.InvokeAsync(changed, fx.Writer);
        conflict.ErrorCode.ShouldBe("invocation-id-conflict");
        var retained = (await fx.Tool.GetApprovalAsync(approval.Plan.InvocationId, fx.Writer)).ShouldNotBeNull();
        retained.ShouldBe(approval);
        File.ReadAllText(fx.FilePath).ShouldBe("original");
    }

    [Theory]
    [InlineData("root")]
    [InlineData("readonly")]
    [InlineData("sandbox")]
    [InlineData("limit")]
    public async Task ResumeApprovedAsync_ConnectionConfigurationChanged_DoesNotReuseApproval(string change)
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeTrue();
        var other = Path.Combine(fx.Root, "another-root");
        Directory.CreateDirectory(other);
        var config = fx.Spec.FileSystem!;
        config = change switch
        {
            "root" => config with { Root = other },
            "readonly" => config with { ReadOnly = true },
            "sandbox" => config with { Sandbox = false },
            _ => config with { MaxReadBytes = 100_000 }
        };
        await fx.Tool.ConnectAsync(fx.Spec with { FileSystem = config }, fx.Writer);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer));
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        File.Exists(Path.Combine(other, "note.txt")).ShouldBeFalse();
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Theory]
    [InlineData("nested/path.txt", "ordinary")]
    [InlineData("../outside.txt", "ordinary")]
    [InlineData("note.txt", "${secrets.account}")]
    public async Task InvokeAsync_UnsupportedApprovalPlan_DoesNotRecordOrWrite(string path, string body)
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var request = FileWriteApprovalScenario.Request() with { Parameters = new() { ["path"] = path }, RawInput = body };
        var result = await fx.Tool.InvokeAsync(request, fx.Writer);
        result.ErrorCode.ShouldBe("approval-operation-unsupported");
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        fx.Sql("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(0);
    }

    [Fact]
    public async Task InvokeAsync_EditCannotBypassConfiguredFileWriteApproval()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var token = fx.Mint("writer", "tool:files:invoke:edit_file");
        var request = FileWriteApprovalScenario.Request() with
        {
            Method = "edit_file",
            Parameters = new() { ["path"] = "note.txt", ["old_string"] = "original", ["new_string"] = "bypass" }
        };
        var result = await fx.Tool.InvokeAsync(request, token);
        result.ErrorCode.ShouldBe("approval-operation-unsupported");
        File.ReadAllText(fx.FilePath).ShouldBe("original");
    }
}
