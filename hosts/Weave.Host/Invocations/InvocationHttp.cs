using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Silo.Invocations;

internal static class InvocationHttp
{
    public const int MaxBodyBytes = 1_048_576;
    private const int MaxCredentialCharacters = 16_384;

    public static bool TryAuthenticate(HttpContext context, string workspaceId, string toolName,
        ICapabilityTokenService tokens, [NotNullWhen(true)] out CapabilityToken? token,
        [NotNullWhen(false)] out IResult? failure)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        token = null;
        failure = Error(401, "invalid-capability");
        var values = context.Request.Headers["X-Weave-Capability"];
        if (values.Count != 1 || values[0] is not { Length: > 0 and <= MaxCredentialCharacters } encoded)
        {
            context.Response.Headers.WWWAuthenticate = "WeaveCapability";
            return false;
        }

        CapabilityToken? presented;
        try
        {
            presented = JsonSerializer.Deserialize(WebEncoders.Base64UrlDecode(encoded),
                InvocationHttpJsonContext.Default.CapabilityToken);
        }
        catch (FormatException)
        {
            context.Response.Headers.WWWAuthenticate = "WeaveCapability";
            return false;
        }
        catch (JsonException)
        {
            context.Response.Headers.WWWAuthenticate = "WeaveCapability";
            return false;
        }
        if (presented is null || !tokens.Validate(presented))
        {
            context.Response.Headers.WWWAuthenticate = "WeaveCapability";
            return false;
        }
        if (!IsRouteSegment(workspaceId) || !IsRouteSegment(toolName))
        {
            failure = Error(400, "invalid-route-identity");
            return false;
        }
        if (!string.Equals(presented.WorkspaceId, workspaceId, StringComparison.Ordinal))
        {
            failure = Error(403, "forbidden");
            return false;
        }
        token = presented with { CancellationToken = context.RequestAborted };
        failure = null;
        return true;
    }

    public static bool TryInvocationId(string value, out InvocationId id)
    {
        if (Guid.TryParseExact(value, "N", out var guid) && guid != Guid.Empty)
        {
            id = InvocationId.From(guid.ToString("N"));
            return true;
        }
        id = default;
        return false;
    }

    public static IResult Error(int status, string code) => Results.Json(
        new InvocationHttpError(code), InvocationHttpJsonContext.Default.InvocationHttpError, statusCode: status);

    private static bool IsRouteSegment(string value) => value.Length is > 0 and <= 128
        && value is not "." and not ".."
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
