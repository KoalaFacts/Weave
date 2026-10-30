namespace Weave.Silo.Api;

/// <summary>Wire payload for <c>event: text</c> SSE frames on the streaming-chat endpoint.</summary>
public sealed record TextEventWire
{
    public required string Text { get; init; }
}
