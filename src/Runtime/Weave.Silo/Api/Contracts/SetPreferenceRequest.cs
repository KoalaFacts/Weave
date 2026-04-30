namespace Weave.Silo.Api;

public sealed record SetPreferenceRequest
{
    public required string Key { get; init; }
    public required string Value { get; init; }
}
