namespace Weave.Security.Tokens;

/// <summary>
/// Single source of truth for capability-grant matching, including the trailing-segment
/// wildcards promised in docs/unique-agent-strategy.md (`tool:*`, `channel:send:*`, `*`).
/// </summary>
public static class CapabilityGrants
{
    public static bool Matches(ICollection<string> grants, string requested)
    {
        if (grants.Count == 0)
            return false;

        if (grants.Contains(requested) || grants.Contains("*"))
            return true;

        var lastColon = requested.LastIndexOf(':');
        while (lastColon > 0)
        {
            if (grants.Contains(requested[..lastColon] + ":*"))
                return true;
            lastColon = requested.LastIndexOf(':', lastColon - 1);
        }

        return false;
    }
}
