using System.Net;

namespace Weave.Silo.Tests.Dashboard;

public sealed class DashboardAuditRenderingTests
{
    [Fact]
    public async Task RenderAudit_RecentRows_DisplaysDecisionEvidenceAndTokenLinks()
    {
        string? route = null;
        await using var fixture = new DashboardPageFixture((request, _) =>
        {
            route = request.RequestUri.ShouldNotBeNull().PathAndQuery;
            return Task.FromResult(DashboardPageFixture.Json("""
                [{"tokenId":"short-id","grant":"invocation:read","issuedTo":"release-reviewer","outcome":"Allow",
                  "actionContext":"read_invocation","reason":"","timestamp":"2030-01-01T08:00:00Z"},
                 {"tokenId":"long-token-123456789","grant":"tool:files:invoke:write_file","issuedTo":"docs-writer",
                  "outcome":"Deny","actionContext":"write_file","reason":"missing-exact-operation-marker","timestamp":"2030-01-01T09:00:00Z"},
                 {"tokenId":"unknown-token","grant":"invocation:read","issuedTo":"pending-reviewer","outcome":"Unknown",
                  "actionContext":"read_invocation","timestamp":"2030-01-01T10:00:00Z"}]
                """));
        });

        var html = await fixture.RenderAsync("Audit");

        route.ShouldBe("/api/audit/capability?limit=200");
        var text = DashboardPageFixture.Text(html);
        text.ShouldContain("3 row(s) — newest first");
        text.ShouldContain("release-reviewer");
        text.ShouldContain("invocation:read");
        text.ShouldContain("Allow");
        text.ShouldContain("Deny");
        text.ShouldContain("Unknown");
        text.ShouldContain("missing-exact-operation-marker");
        text.ShouldContain("short-id");
        text.ShouldContain("long-tok…");
        html.ShouldContain("href=\"/audit/long-token-123456789\"");
        html.ShouldContain("href=\"/audit/short-id\"");
        html.ShouldNotContain("<fluent-progress-ring");
    }

    [Fact]
    public async Task RenderAudit_TokenParameter_QueriesOnlyThatTokenAndLabelsResults()
    {
        string? route = null;
        await using var fixture = new DashboardPageFixture((request, _) =>
        {
            route = request.RequestUri.ShouldNotBeNull().PathAndQuery;
            return Task.FromResult(DashboardPageFixture.Json("""
                [{"tokenId":"review-token-123","grant":"invocation:read","issuedTo":"release-reviewer","outcome":"Allow"}]
                """));
        });

        var html = await fixture.RenderAsync("Audit", new() { ["TokenId"] = "review-token-123" });

        route.ShouldBe("/api/audit/capability/review-token-123");
        var text = DashboardPageFixture.Text(html);
        text.ShouldContain("1 row(s) for token review-t…");
        text.ShouldContain("release-reviewer");
        text.ShouldContain("Clear");
        text.ShouldNotContain("newest first");
    }

    [Theory]
    [InlineData(null, "No capability authorization rows on this silo yet.")]
    [InlineData("review-token", "No audit rows recorded for this token.")]
    public async Task RenderAudit_NoRows_DistinguishesRecentAndFilteredEmptyStates(string? token, string expected)
    {
        await using var fixture = new DashboardPageFixture((_, _) => Task.FromResult(DashboardPageFixture.Json("[]")));

        var html = await fixture.RenderAsync("Audit", new() { ["TokenId"] = token });

        DashboardPageFixture.Text(html).ShouldContain(expected);
        html.ShouldNotContain("<fluent-progress-ring");
        html.ShouldNotContain("Unable to query");
    }

    [Fact]
    public async Task RenderAudit_FailedQuery_ShowsEndpointFailureInsteadOfEmptySuccess()
    {
        await using var fixture = new DashboardPageFixture((_, _) =>
            Task.FromResult(DashboardPageFixture.Json("private-body-marker", HttpStatusCode.Forbidden)));

        var html = await fixture.RenderAsync("Audit");

        var text = DashboardPageFixture.Text(html);
        text.ShouldContain("Unable to query the Weave Silo audit endpoint:");
        text.ShouldContain("403");
        text.ShouldNotContain("No capability authorization rows");
        html.ShouldNotContain("private-body-marker");
        html.ShouldNotContain("<fluent-progress-ring");
    }

    [Fact]
    public async Task RenderAudit_PendingQuery_ReplacesLoadingWithFilteredEmptyState()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new DashboardPageFixture((_, _) => reply.Task);
        var root = await fixture.BeginAsync("Audit", new() { ["TokenId"] = "review-token" });
        string initial;
        try
        {
            initial = await fixture.HtmlAsync(root);
        }
        finally
        {
            reply.TrySetResult(DashboardPageFixture.Json("[]"));
        }
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var html = await fixture.HtmlAsync(root);

        initial.ShouldContain("<fluent-progress-ring");
        DashboardPageFixture.Text(initial).ShouldContain("Loading…");
        html.ShouldNotContain("<fluent-progress-ring");
        DashboardPageFixture.Text(html).ShouldContain("No audit rows recorded for this token.");
    }
}
