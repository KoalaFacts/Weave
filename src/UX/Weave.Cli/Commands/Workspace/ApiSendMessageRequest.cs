namespace Weave.Cli.Commands;

internal sealed record ApiSendMessageRequest
{
    public required string Content { get; init; }
    public string Role { get; init; } = "user";
}
