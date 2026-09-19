namespace Weave.Shared.Strings;

/// <summary>
/// String-sanitization helpers that strip control characters before output.
/// </summary>
/// <remarks>
/// Echoing user-influenced strings into stdout (CLI errors), audit log rows,
/// or telemetry tags risks ANSI escape injection if a malicious value carries
/// terminal control sequences. Replace every <see cref="char.IsControl(char)"/>
/// character with <c>?</c> before formatting. The fast path returns the input
/// unchanged when no control char is present so clean strings allocate nothing.
/// </remarks>
public static class ControlCharFilter
{
    /// <summary>Replaces every control character with <c>?</c>; returns the input unchanged when it contains none.</summary>
    public static string ReplaceControlChars(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!ContainsControl(value))
            return value;

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
        });
    }

    private static bool ContainsControl(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
                return true;
        }
        return false;
    }
}
