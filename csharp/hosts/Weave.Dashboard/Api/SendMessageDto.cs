namespace Weave.Dashboard.Api;

public sealed record SendMessageDto
{
    public required string Content { get; init; }
    public string Role { get; init; } = "user";
}
