using System.Text.Json.Serialization;

namespace Weave.Agents.Chat;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AgentChatTextFrame), nameof(AgentChatTextFrame))]
[JsonDerivedType(typeof(AgentChatCompleteFrame), nameof(AgentChatCompleteFrame))]
public abstract record AgentChatStreamingFrame;

public sealed record AgentChatTextFrame(string Text) : AgentChatStreamingFrame;

public sealed record AgentChatCompleteFrame(AgentChatResponse Response) : AgentChatStreamingFrame;
