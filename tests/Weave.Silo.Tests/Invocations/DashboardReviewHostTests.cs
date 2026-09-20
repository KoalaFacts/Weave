using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Weave.Invocations;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DashboardReview_RealHost_ReturnsExactContentWithoutDecisionOrDispatch(bool globalAuthentication)
    {
        await using var fx = new Fixture(requireApproval: true, globalAuthentication: globalAuthentication);
        await fx.ConnectAsync();
        if (globalAuthentication)
            fx.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fx.GlobalApiSecret);
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fx.Client.DefaultRequestHeaders.Authorization = null;
        var reviewer = fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
        dynamic session = DashboardReviewSessionTests.Session(fx.Client, TimeProvider.System);
        using var lifetime = (IDisposable)session;
        await session.LoadAsync(fx.Workspace, "files", JsonSerializer.Serialize(new
        {
            invocationId = request.InvocationId!.Value.ToString(),
            toolName = request.ToolName,
            method = request.Method,
            parameters = request.Parameters,
            rawInput = request.RawInput
        }), Encode(reviewer), globalAuthentication ? fx.GlobalApiSecret : "", TestContext.Current.CancellationToken);

        ((string?)session.Error).ShouldBeNull();
        ((object?)session.Snapshot).ShouldNotBeNull();
        ((string)session.Snapshot.RawInput).ShouldBe(request.RawInput);
        ((string)session.Snapshot.Subject).ShouldBe("external-agent");
        ((string)session.Snapshot.TargetDescription).ShouldContain(Path.GetDirectoryName(fx.Target)!);
        (await fx.Tool.GetApprovalAsync(request.InvocationId.Value, reviewer)).ShouldNotBeNull()
            .State.ShouldBe(InvocationApprovalState.Pending);
        (await fx.Tool.GetInvocationAsync(request.InvocationId.Value, fx.Token())).ShouldBeNull();
        File.ReadAllText(fx.Target).ShouldBe("original");
        fx.Client.DefaultRequestHeaders.Authorization.ShouldBeNull();
    }
}
