namespace Weave.Cli.Commands;

internal sealed record ApiConversationMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
