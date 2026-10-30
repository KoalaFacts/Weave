namespace Weave.Actions.Workspace;

public static class WorkspaceCapabilityHttp
{
    public static bool IsSecureOrigin(Uri? uri) => uri is { IsAbsoluteUri: true }
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath == "/";

    public static bool IsEncodedToken(string value) => value.Length is > 0 and <= 16_384
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        UseDefaultCredentials = false,
        CheckCertificateRevocationList = true
    };
}
