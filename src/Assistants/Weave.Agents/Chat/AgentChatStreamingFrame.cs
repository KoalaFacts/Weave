namespace Weave.Agents.Chat;

public abstract record AgentChatStreamingFrame;

public sealed record AgentChatTextFrame(string Text) : AgentChatStreamingFrame;

public sealed record AgentChatCompleteFrame(AgentChatResponse Response) : AgentChatStreamingFrame;
