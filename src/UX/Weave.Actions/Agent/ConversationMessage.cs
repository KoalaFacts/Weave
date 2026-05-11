namespace Weave.Actions.Agent;

/// <summary>
/// Frontend-facing view of one entry in the agent's conversation history.
/// Translated from the silo's wire shape inside
/// <see cref="SendMessageAction"/> so the action layer stays oblivious to
/// the wire.
/// </summary>
public sealed record ConversationMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
