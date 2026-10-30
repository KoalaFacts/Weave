namespace Weave.Actions.Config;

/// <summary>
/// Canonical names of the config keys recognized by the action layer.
/// </summary>
/// <remarks>
/// The <c>All</c> set is the read surface for <see cref="GetConfigAction"/>'s
/// single-key path; the <c>Writable</c> subset is what <c>SetConfigAction</c>
/// accepts. Both lookups are case-insensitive; this list is the canonical-case
/// rendering frontends (e.g., shell completion sources) can surface to users.
/// </remarks>
public static class ConfigKeys
{
    public static IReadOnlyList<string> All { get; } =
    [
        "version",
        "defaultPort",
        "storage",
        "authMode",
        "requireHttps",
        "siloPath",
        "weaveHome",
        "baseUrl"
    ];

    /// <summary>
    /// The subset of <see cref="All"/> that <c>SetConfigAction</c> accepts.
    /// </summary>
    /// <remarks>
    /// The unwritable members are derived (<c>baseUrl</c> from env / port,
    /// <c>weaveHome</c> from the user profile, <c>version</c> from the
    /// installed assembly) or are runtime-shaped concerns (<c>storage</c>,
    /// <c>authMode</c>, <c>requireHttps</c>) that have richer setup flows
    /// (<c>weave storage change</c>, <c>weave init</c>) than a single key/
    /// value pair.
    /// </remarks>
    public static IReadOnlyList<string> Writable { get; } =
    [
        "siloPath",
        "defaultPort"
    ];
}

