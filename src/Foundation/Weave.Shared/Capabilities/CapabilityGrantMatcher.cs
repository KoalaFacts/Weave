namespace Weave.Shared.Capabilities;

/// <summary>
/// Segment-wise wildcard match for capability grant strings. Shared by token
/// validation and manifest-side authorization so both gates resolve grants
/// identically.
/// </summary>
public static class CapabilityGrantMatcher
{
    /// <summary>
    /// Returns <c>true</c> when any string in <paramref name="owned"/> covers
    /// <paramref name="requested"/>. Each <c>*</c> matches one segment; a
    /// trailing <c>*</c> matches one or more.
    /// </summary>
    public static bool HasGrant(IEnumerable<string> owned, string requested)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(requested);

        foreach (var o in owned)
        {
            if (o == requested || Matches(o, requested))
                return true;
        }
        return false;
    }

    private static bool Matches(string owned, string requested)
    {
        var ownedSegs = owned.Split(':');
        var requestedSegs = requested.Split(':');

        for (var i = 0; i < ownedSegs.Length; i++)
        {
            var seg = ownedSegs[i];
            var isLast = i == ownedSegs.Length - 1;

            if (isLast && seg == "*")
                return requestedSegs.Length > i;

            if (i >= requestedSegs.Length)
                return false;

            if (seg != "*" && seg != requestedSegs[i])
                return false;
        }

        return ownedSegs.Length == requestedSegs.Length;
    }
}
