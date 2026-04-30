namespace Weave.Silo.Api;

public sealed record SetDomainContextRequest
{
    public required string Key { get; init; }
    public required string Value { get; init; }
}
