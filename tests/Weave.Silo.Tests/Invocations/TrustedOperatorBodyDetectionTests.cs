using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Weave.Silo.Operator;

namespace Weave.Silo.Tests.Invocations;

public sealed class TrustedOperatorBodyDetectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HasInput_ServerReportsHttp2BodyWithoutFramingHeaders_UsesServerEvidence(bool hasBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Protocol = "HTTP/2";
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(hasBody));
        context.Request.ContentLength.ShouldBeNull();
        context.Request.Headers.ContainsKey("Transfer-Encoding").ShouldBeFalse();

        ExtensionsToTrustedOperator.HasInput(context).ShouldBe(hasBody);
    }

    // The server feature is the contract: unlike HttpClient, it cannot synthesize a length header here.
    private sealed class BodyDetection(bool hasBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => hasBody;
    }
}
