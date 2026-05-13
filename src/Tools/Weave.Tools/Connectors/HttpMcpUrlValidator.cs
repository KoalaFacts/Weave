using System.Net;
using System.Net.Sockets;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Connectors;

// SSRF + URL-shape gate for HttpMcpTransport. Lifted out of the transport
// because the policy is independent of the transport and reusable for any
// future MCP transport that takes a peer URL (websocket, gRPC-web, …).
//
// Defense in depth — defenses fire in this order:
//   1. http/https scheme only.
//   2. No userinfo (blocks `http://a:b@host/`).
//   3. No fragment (blocks ambiguity from server-side handling).
//   4. Unless AllowPrivateEndpoints, reject literal loopback, private,
//      link-local, CGNAT, multicast, and reserved IPs (v4 + v6).
//
// DNS hostnames are intentionally accepted as-is — resolving here invites
// TOCTOU / DNS-rebinding hazards, and infra-level egress controls are the
// right defense for the "name resolves to private IP" attack class.
internal static class HttpMcpUrlValidator
{
    public static Uri Validate(McpConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
            throw new InvalidOperationException("HttpMcpTransport requires McpConfig.Url");

        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"McpConfig.Url '{config.Url}' is not an absolute http(s) URL.");
        }

        if (uri.UserInfo.Length > 0)
            throw new InvalidOperationException("McpConfig.Url must not contain userinfo (user:password@host).");

        if (uri.Fragment.Length > 0)
            throw new InvalidOperationException("McpConfig.Url must not contain a fragment.");

        if (!config.AllowPrivateEndpoints)
            RejectPrivateEndpoint(uri);

        return uri;
    }

    private static void RejectPrivateEndpoint(Uri uri)
    {
        if (uri.IsLoopback)
            throw new InvalidOperationException(
                $"McpConfig.Url '{uri}' targets loopback. Set AllowPrivateEndpoints=true in the manifest to opt in (intended for local development).");

        if (IPAddress.TryParse(uri.Host, out var ip) && IsPrivateOrReserved(ip))
        {
            throw new InvalidOperationException(
                $"McpConfig.Url '{uri}' targets a private or reserved IP. Set AllowPrivateEndpoints=true to opt in.");
        }
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        // Loopback (127/8, ::1) is caught by uri.IsLoopback in the caller — not
        // re-checked here. Leaving it out makes the ranges below the single
        // source of truth and avoids a dead branch.
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;
            if (b[0] == 10) return true;
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 172 && (b[1] & 0xF0) == 16) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] >= 224) return true;
            return false;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
            return false;
        }
        return false;
    }
}
