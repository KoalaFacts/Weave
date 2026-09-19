namespace Weave.Shared.VirtualActors;

public readonly record struct VirtualActorId(string Value)
{
    public static VirtualActorId From(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Virtual actor id cannot be empty.", nameof(value));

        return new VirtualActorId(value);
    }

    public static VirtualActorId Combine(params object[] parts)
    {
        if (parts.Length == 0)
            throw new ArgumentException("Virtual actor id requires at least one part.", nameof(parts));

        var values = parts.Select(static part => part?.ToString()).ToArray();
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Virtual actor id parts cannot be empty.", nameof(parts));

        return new VirtualActorId(string.Join("/", values));
    }

    public override string ToString() => Value;
}
