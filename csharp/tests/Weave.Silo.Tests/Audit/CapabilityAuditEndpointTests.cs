using System.Net;
using System.Text.Json;

namespace Weave.Silo.Tests.Audit;

/// <summary>
/// End-to-end test that the CapabilityAuthorizer publishes audit rows,
/// the subscriber writes them to the store, and the HTTP endpoint serves
/// them back. Exercises every link in roadmap #3 in one pass.
/// </summary>
public sealed class CapabilityAuditEndpointTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public CapabilityAuditEndpointTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task GetRecent_AfterSkillRead_IncludesSkillReadGrantRow()
    {
        using var client = _factory.CreateClient();
        var ws = $"ws-{Guid.NewGuid():N}";

        // GET skills mints a skill:read token, calls SkillMemoryActor.GetAllSkillsAsync,
        // which calls CapabilityAuthorizer.AuthorizeAsync(..., "skill:read", ws, "GetAllSkillsAsync").
        // That publishes a CapabilityAuthorizationEvent the audit subscriber records.
        using var skillsResponse = await client.GetAsync(
            $"/api/workspaces/{ws}/skills",
            TestContext.Current.CancellationToken);
        skillsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var recentResponse = await client.GetAsync(
            "/api/audit/capability?limit=200",
            TestContext.Current.CancellationToken);
        recentResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var recentBody = await recentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var recentDoc = JsonDocument.Parse(recentBody);
        var workspaceRow = recentDoc.RootElement.EnumerateArray()
            .FirstOrDefault(r =>
                r.GetProperty("workspaceId").GetString() == ws
                && r.GetProperty("grant").GetString() == "skill:read");

        workspaceRow.ValueKind.ShouldNotBe(JsonValueKind.Undefined, recentBody);
        workspaceRow.GetProperty("outcome").GetString().ShouldBe("Allow");
        workspaceRow.GetProperty("actionContext").GetString().ShouldBe("GetAllSkillsAsync");
    }

    [Fact]
    public async Task GetByToken_ReturnsTraceForAGivenToken()
    {
        using var client = _factory.CreateClient();
        var ws = $"ws-{Guid.NewGuid():N}";

        using var skillsResponse = await client.GetAsync(
            $"/api/workspaces/{ws}/skills",
            TestContext.Current.CancellationToken);
        skillsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var recentResponse = await client.GetAsync(
            "/api/audit/capability?limit=500",
            TestContext.Current.CancellationToken);
        var recentBody = await recentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var recentDoc = JsonDocument.Parse(recentBody);
        var ourRow = recentDoc.RootElement.EnumerateArray()
            .FirstOrDefault(r =>
                r.GetProperty("workspaceId").GetString() == ws
                && r.GetProperty("grant").GetString() == "skill:read");
        ourRow.ValueKind.ShouldNotBe(JsonValueKind.Undefined, recentBody);

        var tokenId = ourRow.GetProperty("tokenId").GetString()!;

        using var byTokenResponse = await client.GetAsync(
            $"/api/audit/capability/{tokenId}",
            TestContext.Current.CancellationToken);
        byTokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var byTokenBody = await byTokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var byTokenDoc = JsonDocument.Parse(byTokenBody);
        var rows = byTokenDoc.RootElement.EnumerateArray().ToList();
        rows.Count.ShouldBeGreaterThan(0, byTokenBody);
        rows.ShouldAllBe(r => r.GetProperty("tokenId").GetString() == tokenId);
    }

    [Fact]
    public async Task GetByToken_UnknownToken_ReturnsEmpty()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/audit/capability/no-such-token",
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetArrayLength().ShouldBe(0);
    }
}
