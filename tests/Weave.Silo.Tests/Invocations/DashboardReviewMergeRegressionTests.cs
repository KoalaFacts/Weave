using System.Net;
using System.Text;
using System.Text.Json;

namespace Weave.Silo.Tests.Invocations;

public sealed class DashboardReviewMergeRegressionTests
{
    private const string Id = "abcdef0123456789abcdef0123456789";

    [Fact]
    public void Deserialize_DashboardSnapshot_ParametersRemainReadOnly()
    {
        var type = DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Approvals.ApprovalReviewSnapshot");
        var json = JsonSerializer.Serialize(new
        {
            InvocationId = Id,
            Parameters = new Dictionary<string, string> { ["path"] = "note.txt" },
            PlanDigest = "original-digest"
        });
        var snapshot = JsonSerializer.Deserialize(json, type).ShouldNotBeNull();
        var parameters = type.GetProperty("Parameters")!.GetValue(snapshot)
            .ShouldBeAssignableTo<IDictionary<string, string>>();

        parameters.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => parameters["path"] = "tampered.txt");
        parameters["path"].ShouldBe("note.txt");
        type.GetProperty("PlanDigest")!.GetValue(snapshot).ShouldBe("original-digest");
    }

    [Fact]
    public async Task LoadAsync_MalformedOriginalJson_ReportsInputErrorWithoutContactingHost()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Reply());
        })) { BaseAddress = new Uri("http://localhost/") };
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;

        await session.LoadAsync("workspace", "files", "{malformed", "test-capability", "", TestContext.Current.CancellationToken);

        calls.ShouldBe(0);
        ((object?)session.Snapshot).ShouldBeNull();
        var error = ((string?)session.Error).ShouldNotBeNull();
        error.ShouldContain("complete original invocation JSON");
        error.ShouldNotContain("configured server");
    }

    [Theory]
    [InlineData("test@secret")]
    [InlineData("test:secret")]
    [InlineData("test/secret+value==")]
    public async Task LoadAsync_ConfiguredBearerPunctuation_IsHandledWithoutLosingReview(string bearer)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            request.Headers.GetValues("Authorization").Single().ShouldBe("Bearer " + bearer);
            return Task.FromResult(Reply());
        })) { BaseAddress = new Uri("http://localhost/") };
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;
        var json = JsonSerializer.Serialize(new
        {
            invocationId = Id,
            toolName = "files",
            method = "write_file",
            parameters = new Dictionary<string, string> { ["path"] = "note.txt" },
            rawInput = "reviewed text"
        });

        await session.LoadAsync("workspace", "files", json, "test-capability", bearer, TestContext.Current.CancellationToken);

        ((string?)session.Error).ShouldBeNull();
        ((object?)session.Snapshot).ShouldNotBeNull();
        calls.ShouldBe(1);
        http.DefaultRequestHeaders.Authorization.ShouldBeNull();
    }

    private static HttpResponseMessage Reply() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            invocationId = Id,
            workspaceId = "workspace",
            subject = "original-agent",
            toolName = "files",
            operation = "write_file",
            targetDescription = "registered target",
            parameters = new Dictionary<string, string> { ["path"] = "note.txt" },
            rawInput = "reviewed text",
            planDigest = "test-plan",
            expiresAt = TimeProvider.System.GetUtcNow().AddMinutes(5)
        }), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
