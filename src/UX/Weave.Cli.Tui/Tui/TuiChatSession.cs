using System.Text;
using Weave.Actions.Agent;
using Weave.Actions.Context;


namespace Weave.Cli.Tui;

internal sealed class TuiChatSession
{
    private readonly SendMessageStreamingAction _streamingAction;
    private readonly TimeProvider _timeProvider;
    private readonly List<ConversationMessage> _history = [];

    public TuiChatSession(SendMessageStreamingAction streamingAction, TimeProvider timeProvider)
    {
        _streamingAction = streamingAction;
        _timeProvider = timeProvider;
    }

    /// <summary>Test seam — read-only view of the conversation history.</summary>
    internal IReadOnlyList<ConversationMessage> History => _history;

    public void Clear() => _history.Clear();

    public void ShowHistory(TuiSession session) => RenderHistory(session, _history);

    public async Task SendAsync(
        TuiSession session,
        string message,
        CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted(
                $"Workspace '{session.WorkspaceName}' is not running. Type /up to start it.");
            return;
        }
        if (session.AgentName is null)
        {
            CliTheme.WriteMuted("No agent selected. Try: /agents, then /use <agent>");
            return;
        }

        CliTheme.WriteUserEcho(message);
        CliTheme.WriteAgentReplyBegin(session.AgentName);

        var assembled = new StringBuilder();
        SendMessageResult? completed = null;
        ActionFailure? failure = null;

        await foreach (var chunk in _streamingAction.StreamAsync(
            new SendMessageInput(session.WorkspaceId!, session.AgentName, message), ct))
        {
            switch (chunk)
            {
                case SendMessageTextChunk text:
                    CliTheme.WriteAgentReplyChunk(text.Text);
                    assembled.Append(text.Text);
                    break;
                case SendMessageCompleteChunk done:
                    completed = done.Result;
                    break;
                case SendMessageErrorChunk error:
                    failure = error.Failure;
                    break;
            }
        }

        CliTheme.WriteAgentReplyEnd();

        if (failure is not null)
        {
            // User-initiated cancellation (Ctrl-C) shouldn't render as an error.
            if (failure.Reason != ActionFailureReason.Cancelled)
                CliTheme.WriteError($"Agent call failed: {failure.Message}");
            return;
        }

        // SendMessageStreamingAction's terminal-error invariant guarantees one of
        // {failure, completed} is set when the loop exits — failure handled above.
        var reply = completed!;
        var now = _timeProvider.GetUtcNow();
        _history.Add(new ConversationMessage { Role = "user", Content = message, Timestamp = now });
        _history.Add(new ConversationMessage { Role = "assistant", Content = reply.Content, Timestamp = now });
        if (reply.Messages is { Count: > 0 })
        {
            _history.Clear();
            _history.AddRange(reply.Messages);
        }

        if (reply.UsedTools)
            CliTheme.WriteMuted("  Tools were used to generate this response.");

        if (!string.IsNullOrWhiteSpace(reply.Model))
            CliTheme.WriteMuted($"  Model: {reply.Model}");
    }

    private static void RenderHistory(
        TuiSession session,
        List<ConversationMessage> conversationHistory)
    {
        if (session.AgentName is null)
        {
            CliTheme.WriteMuted("No agent selected. Use /use <agent> first.");
            return;
        }

        if (conversationHistory.Count == 0)
        {
            CliTheme.WriteMuted("No conversation history yet. Send a message first.");
            return;
        }

        CliTheme.WriteSection($"History · {session.AgentName}");
        foreach (var msg in conversationHistory)
        {
            if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                CliTheme.WriteUserEcho(msg.Content);
            else
                CliTheme.WriteAgentReply(session.AgentName, msg.Content);
        }
    }
}
