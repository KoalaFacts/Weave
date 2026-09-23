using System.Runtime.CompilerServices;

namespace Weave.Actions.Agent;

/// <summary>
/// Minimal W3C-spec SSE line reader scoped to the silo streaming-chat endpoint.
/// Yields one <see cref="SseEvent"/> per blank-line-terminated frame; ignores
/// comment lines (leading <c>:</c>) and field lines without a colon.
/// </summary>
/// <remarks>
/// A sibling parser exists at <c>Weave.Agents.Pipeline.Providers.Anthropic.AnthropicSseParser</c>
/// for the upstream LLM connection. They aren't shared because the actions layer
/// must not pull pipeline internals (see CLAUDE.md dependency-flow rules); the
/// W3C SSE wire format is small enough that two ~35-line copies beats a shared
/// abstraction.
/// </remarks>
internal readonly record struct SseEvent(string Event, string Data);

internal static class SseLineReader
{
    public static async IAsyncEnumerable<SseEvent> ReadEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        string? eventName = null;
        string? data = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (eventName is not null && data is not null)
                    yield return new SseEvent(eventName, data);
                eventName = null;
                data = null;
                continue;
            }

            if (line[0] == ':')
                continue;

            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            var field = line[..colon];
            var value = colon + 1 < line.Length && line[colon + 1] == ' '
                ? line[(colon + 2)..]
                : line[(colon + 1)..];

            if (field == "event")
                eventName = value;
            else if (field == "data")
                data = data is null ? value : data + "\n" + value;
        }

        if (eventName is not null && data is not null)
            yield return new SseEvent(eventName, data);
    }
}
