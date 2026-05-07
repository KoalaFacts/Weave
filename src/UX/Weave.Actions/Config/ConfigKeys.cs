namespace Weave.Actions.Config;

/// <summary>
/// Canonical names of the keys recognized by <see cref="GetConfigAction"/>'s
/// single-key path. The action's lookup is case-insensitive; this list is the
/// canonical-case rendering that frontends (e.g., shell completion sources)
/// can surface to users.
/// </summary>
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
}
