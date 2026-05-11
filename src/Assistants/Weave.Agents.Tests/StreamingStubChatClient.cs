using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Weave.Agents.Tests;

internal sealed class StreamingStubChatClient : IChatClient
{
    public IReadOnlyList<ChatResponseUpdate> Updates { get; set; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming stub does not implement GetResponseAsync.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in Updates)
        {
            yield return update;
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
