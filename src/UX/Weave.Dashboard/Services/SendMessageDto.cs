namespace Weave.Dashboard.Services;

public sealed record SendMessageDto
{
    public required string Content { get; init; }
    public string Role { get; init; } = "user";
}
