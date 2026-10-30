namespace Weave.Dashboard.Approvals;

public sealed record ApprovalReviewOptions(bool Enabled, Uri? Upstream)
{
    public static ApprovalReviewOptions Read(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>("Weave:Review:Enabled");
        return new(enabled, enabled ? ParseUpstream(configuration["Weave:Review:BaseUrl"]) : null);
    }

    public static Uri ParseUpstream(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/")
            throw new InvalidOperationException("Review requires a fixed HTTPS origin, or HTTP loopback for local development, without credentials, path, query or fragment.");
        return uri;
    }

    public static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        UseDefaultCredentials = false,
        CheckCertificateRevocationList = true
    };
}
