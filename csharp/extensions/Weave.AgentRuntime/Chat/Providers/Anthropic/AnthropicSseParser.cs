using System.Runtime.CompilerServices;

namespace Weave.Agents.Pipeline.Providers.Anthropic;

internal static class AnthropicSseParser
{
    public static async IAsyncEnumerable<AnthropicSseEvent> ReadEventsAsync(
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
                    yield return new AnthropicSseEvent(eventName, data);
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
            yield return new AnthropicSseEvent(eventName, data);
    }
}

internal readonly record struct AnthropicSseEvent(string Event, string Data);
