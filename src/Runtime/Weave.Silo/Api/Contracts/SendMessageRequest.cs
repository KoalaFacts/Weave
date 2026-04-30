namespace Weave.Silo.Api;

public sealed record SendMessageRequest
{
    public required string Content { get; init; }
    public string Role { get; init; } = "user";
}
