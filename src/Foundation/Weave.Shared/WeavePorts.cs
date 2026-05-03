namespace Weave.Shared;

/// <summary>
/// Default port assignments for Weave services.
/// All ports live in the 94xx range to avoid conflicts with
/// well-known services (ASP.NET 5000, Kubernetes NodePort 30000+, etc.).
/// </summary>
public static class WeavePorts
{
    // ── Local development ─────────────────────────────────────────
    public const int SiloHttps = 9400;
    public const int SiloHttp = 9401;
    public const int DashboardHttps = 9402;
    public const int DashboardHttp = 9403;

    // ── Orleans clustering ────────────────────────────────────────
    public const int OrleansSilo = 9410;
    public const int OrleansGateway = 9411;

    // ── Infrastructure (standard third-party defaults) ────────────
    public const int Redis = 6379;

    /// <summary>
    /// Returns all port assignments with their descriptions.
    /// </summary>
    public static IReadOnlyList<(string Name, int Port, string Description)> All =>
    [
        ("silo-https", SiloHttps, "Silo HTTPS (local dev)"),
        ("silo-http", SiloHttp, "Silo HTTP"),
        ("dashboard-https", DashboardHttps, "Dashboard HTTPS (local dev)"),
        ("dashboard-http", DashboardHttp, "Dashboard HTTP (local dev)"),
        ("orleans-silo", OrleansSilo, "Orleans silo-to-silo clustering"),
        ("orleans-gateway", OrleansGateway, "Orleans client gateway"),
        ("redis", Redis, "Redis cache (standard default)"),
    ];
}
