using System.Net;
using System.Net.Http.Json;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class TrustedOperatorOnboardingTests
{
    [Theory]
    [InlineData("tools/files/connect")]
    [InlineData("credentials/writer/issue")]
    public async Task Post_Http2BodyWithoutLength_CannotBeTreatedAsAnEmptyProfileRequest(string operation)
    {
        await using var fx = new Fixture();
        fx.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/operator/" + operation)
        {
            Version = HttpVersion.Version20,
            Content = JsonContent.Create(new { issuedTo = "injected", root = "/" })
        };
        request.Headers.Add("X-Weave-Operator-Key", fx.OperatorKey);
        request.Content.Headers.ContentLength.ShouldBeNull();
        request.Headers.TransferEncodingChunked.ShouldNotBe(true);

        using var response = await fx.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Post_Http2WithoutBody_PreservesConfiguredProfileIssuance()
    {
        await using var fx = new Fixture();
        fx.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/operator/credentials/reader/issue")
        {
            Version = HttpVersion.Version20
        };
        request.Headers.Add("X-Weave-Operator-Key", fx.OperatorKey);

        using var response = await fx.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }
}
