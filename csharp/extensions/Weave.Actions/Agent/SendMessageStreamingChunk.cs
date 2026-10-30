using Weave.Actions.Context;

namespace Weave.Actions.Agent;

/// <summary>
/// One chunk emitted by <see cref="SendMessageStreamingAction"/>. Tagged-union
/// over <see cref="SendMessageTextChunk"/> (per-delta text), <see cref="SendMessageCompleteChunk"/>
/// (terminal frame carrying the final <see cref="SendMessageResult"/>), and
/// <see cref="SendMessageErrorChunk"/> (transport or silo failure — terminal).
/// </summary>
/// <remarks>
/// <para>Frontends consume the stream with a single <c>await foreach</c> and pattern-match
/// on the chunk type. On <see cref="SendMessageErrorChunk"/> the stream ends; on
/// <see cref="SendMessageCompleteChunk"/> the stream ends and the carried result is the
/// authoritative reply (matching the unary <see cref="SendMessageAction"/> shape).</para>
///
/// <para>A successful stream emits zero or more text chunks followed by exactly one
/// complete chunk. The terminal-error invariant means a stream that yields an error
/// chunk will yield no further chunks — the action stops the underlying SSE read.</para>
/// </remarks>
public abstract record SendMessageStreamingChunk;

public sealed record SendMessageTextChunk(string Text) : SendMessageStreamingChunk;

public sealed record SendMessageCompleteChunk(SendMessageResult Result) : SendMessageStreamingChunk;

public sealed record SendMessageErrorChunk(ActionFailure Failure) : SendMessageStreamingChunk;
