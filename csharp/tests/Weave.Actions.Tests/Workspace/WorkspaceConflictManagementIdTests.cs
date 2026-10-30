using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;
using Weave.Actions.Workspace;
using Weave.Workspaces.Manifest;

namespace Weave.Actions.Tests.Workspace;

public sealed class WorkspaceConflictManagementIdTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExecuteAsync_Conflict_PreservesSubmittedIdAndRequiresInspection(bool start, bool detail)
    {
        string? submittedId = null;
        var requests = 0;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests++;
            submittedId = request.Headers.GetValues("X-Weave-Management-Id").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(detail
                    ? """{"detail":"runtime refused after admission"}"""
                    : """{"title":"Conflict"}""")
            });
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        var failure = start
            ? (await new StartWorkspaceAction(client).ExecuteAsync(
                new StartWorkspaceInput(new WorkspaceManifest { Name = "demo", Version = "1.0" }),
                TestContext.Current.CancellationToken)).Failure
            : (await new StopWorkspaceAction(client).ExecuteAsync(new StopWorkspaceInput("workspace"),
                TestContext.Current.CancellationToken)).Failure;

        failure.ShouldNotBeNull();
        failure.Reason.ShouldBe(ActionFailureReason.Conflict);
        submittedId.ShouldNotBeNull();
        Guid.TryParseExact(submittedId, "N", out _).ShouldBeTrue();
        failure.Message.ShouldContain($"Inspect management operation {submittedId} before retrying.");
        failure.Message.ShouldContain(detail ? "runtime refused after admission"
            : start ? "already running or in a conflicting state" : "could not be stopped");
        requests.ShouldBe(1);
    }
}
