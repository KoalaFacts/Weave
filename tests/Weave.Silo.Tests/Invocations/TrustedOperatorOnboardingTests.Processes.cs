using System.Net;
using Weave.Tools.Tests.Processes;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class TrustedOperatorOnboardingTests
{
    private const string ProcessRoute = "/api/operator/runtime/processes";

    [Fact]
    public async Task ObserveProcesses_OperatorAuthenticated_ReturnsNoncacheableHostCapacity()
    {
        await using var fx = new Fixture();
        fx.Start();
        using var response = await fx.SendAsync(HttpMethod.Get, ProcessRoute, admin: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        var snapshot = await JsonAsync(response);
        snapshot.GetProperty("capacity").GetInt32().ShouldBe(8);
        snapshot.GetProperty("available").GetInt32().ShouldBe(8);
        snapshot.GetProperty("executions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ObserveProcesses_RealChildRunning_UsesSharedRunnerWithoutExposingCommandData()
    {
        await using var fx = new Fixture();
        fx.Start();
        using var child = new ProcessTestChild("wait");
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = fx.Processes.RunAsync("node", child.Arguments, abort.Token);
        try
        {
            await child.GetProcessAsync(running);
            using var response = await fx.SendAsync(HttpMethod.Get, ProcessRoute, admin: true);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var snapshot = await JsonAsync(response);
            snapshot.GetProperty("available").GetInt32().ShouldBe(7);
            var execution = snapshot.GetProperty("executions").EnumerateArray().ShouldHaveSingleItem();
            execution.GetProperty("phase").GetString().ShouldBe("Running");
            execution.GetProperty("exit").GetString().ShouldBe("Pending");
            execution.GetProperty("termination").GetString().ShouldBe("NotStarted");
            Guid.Parse(execution.GetProperty("executionId").GetString()!).ShouldNotBe(Guid.Empty);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            body.ShouldNotContain(child.Root);
            body.ShouldNotContain("process-child.ts");
            body.ShouldNotContain("ready");
            body.ShouldNotContain(fx.OperatorKey);
            body.ShouldNotContain("signature");
        }
        finally
        {
            await abort.CancelAsync();
            var canceled = await Should.ThrowAsync<OperationCanceledException>(() => running);
            canceled.CancellationToken.ShouldBe(abort.Token);
        }
        using var idle = await fx.SendAsync(HttpMethod.Get, ProcessRoute, admin: true);
        idle.StatusCode.ShouldBe(HttpStatusCode.OK);
        var completed = await JsonAsync(idle);
        completed.GetProperty("available").GetInt32().ShouldBe(8);
        completed.GetProperty("executions").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("duplicate")]
    [InlineData("agent-capability")]
    public async Task ObserveProcesses_WithoutValidOperatorAuthority_IsDenied(string condition)
    {
        await using var fx = new Fixture();
        fx.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, ProcessRoute);
        if (condition is "wrong" or "duplicate")
            request.Headers.TryAddWithoutValidation("X-Weave-Operator-Key", condition == "wrong" ? "wrong-key" : fx.OperatorKey);
        if (condition == "duplicate")
            request.Headers.TryAddWithoutValidation("X-Weave-Operator-Key", fx.OperatorKey);
        if (condition == "agent-capability")
            request.Headers.Add("X-Weave-Capability", await fx.IssueAsync("reader"));
        using var response = await fx.Client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain("executions");
        body.ShouldNotContain(fx.OperatorKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserveProcesses_OperatorDisabled_RouteIsAbsent(bool agentOnly)
    {
        await using var fx = new Fixture(enabled: false, condition: agentOnly ? "agent-only" : null);
        fx.Start();
        using var response = await fx.SendAsync(HttpMethod.Get, ProcessRoute, admin: true);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ObserveProcesses_GlobalAuthenticationEnabled_RequiresBothCredentials()
    {
        await using var fx = new Fixture(globalAuthentication: true);
        fx.Start();
        using var noGlobal = await fx.SendAsync(HttpMethod.Get, ProcessRoute, admin: true);
        noGlobal.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        fx.Client.DefaultRequestHeaders.Authorization = new("Bearer", fx.GlobalSecret);
        using var noOperator = await fx.SendAsync(HttpMethod.Get, ProcessRoute);
        noOperator.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var allowed = await fx.SendAsync(HttpMethod.Get, ProcessRoute, admin: true);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(allowed)).GetProperty("available").GetInt32().ShouldBe(8);
    }

    [Fact]
    public async Task ObserveProcesses_UntrustedHttpWithForwardingHeaders_IsDenied()
    {
        await using var fx = new Fixture();
        fx.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost" + ProcessRoute);
        request.Headers.Add("X-Weave-Operator-Key", fx.OperatorKey);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        using var response = await fx.Client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("executions");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserveProcesses_RequestHasInput_RejectsUnrecognizedFiltering(bool body)
    {
        await using var fx = new Fixture();
        fx.Start();
        using var response = await fx.SendAsync(HttpMethod.Get, body ? ProcessRoute : ProcessRoute + "?workspaceId=another",
            body ? new { workspaceId = "another" } : null, admin: true);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("process-observation-request-must-be-empty");
    }
}
