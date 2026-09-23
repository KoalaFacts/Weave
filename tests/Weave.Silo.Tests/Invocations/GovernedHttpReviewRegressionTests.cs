using System.Net;
using Weave.Invocations;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Fact]
    public async Task Post_TrailingSlashPending_FollowingLocationReturnsApproval()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route + "/", request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var location = pending.Headers.Location.ShouldNotBeNull().ToString();
        location.ShouldBe(fx.Route + "/" + request.InvocationId + "/approval");
        using var status = await fx.SendAsync(HttpMethod.Get, location, null, fx.Token());
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(status)).GetProperty("approvalState").GetString().ShouldBe("Pending");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Post_ReconnectedSandboxConfiguration_OnlyEquivalentTargetUsesApproval(bool originalSandbox, bool resumedSandbox)
    {
        await using var fx = new Fixture(requireApproval: true);
        ToolSpec Spec(bool sandbox) => new()
        {
            Name = "files",
            Type = ToolType.FileSystem,
            FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig
            {
                Root = Path.GetDirectoryName(fx.Target)!,
                Sandbox = sandbox
            }
        };
        await fx.Tool.ConnectAsync(Spec(originalSandbox), fx.Token(grants: ["tool:files:connect"]));
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var approver = fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
        var id = request.InvocationId!.Value;
        var approval = (await fx.Tool.GetApprovalAsync(id, approver)).ShouldNotBeNull();
        (await fx.Tool.DecideApprovalAsync(id, approval.PlanDigest,
            InvocationApprovalDecision.Approve, approver)).Succeeded.ShouldBeTrue();
        await fx.Tool.DisconnectAsync();
        await fx.Tool.ConnectAsync(Spec(resumedSandbox), fx.Token(grants: ["tool:files:connect"]));

        using var resumed = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());

        if (originalSandbox == resumedSandbox)
        {
            resumed.StatusCode.ShouldBe(HttpStatusCode.OK);
            File.ReadAllText(fx.Target).ShouldBe("reviewed text");
        }
        else
        {
            resumed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var body = await JsonAsync(resumed);
            body.GetProperty("errorCode").GetString().ShouldBe("approval-plan-conflict");
            body.TryGetProperty("attemptId", out _).ShouldBeFalse();
            File.ReadAllText(fx.Target).ShouldBe("original");
        }
    }
}
