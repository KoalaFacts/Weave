using Microsoft.AspNetCore.Http.Features;

namespace Weave.Silo.Invocations;

internal static class ProposalHttp
{
    public static bool HasUnexpectedInput(HttpContext context) =>
        context.Request.QueryString.HasValue
        || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
        || context.Request.ContentLength > 0;

    public static IResult Unavailable(string code) => InvocationHttp.Error(code switch
    {
        "proposal-not-found" or "approval-not-found" => 404,
        "invalid-invocation-id" or "invalid-invocation" => 400,
        _ => 409
    }, code);
}
