using Spectre.Console;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal sealed class TuiChatSession
{
    private readonly SendMessageAction _sendMessageAction;
    private readonly List<ConversationMessage> _history = [];

    public TuiChatSession(SendMessageAction sendMessageAction)
    {
        _sendMessageAction = sendMessageAction;
    }

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

        ActionResult<SendMessageResult> result = default;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(CliTheme.AccentStyle)
            .StartAsync($"{session.AgentName} is thinking…", async _ =>
            {
                result = await _sendMessageAction.ExecuteAsync(
                    new SendMessageInput(session.WorkspaceId!, session.AgentName, message),
                    ct);
            });

        if (!result.IsSuccess)
        {
            // User-initiated cancellation (Ctrl-C) shouldn't render as an error.
            if (result.Failure.Reason != ActionFailureReason.Cancelled)
                CliTheme.WriteError($"Agent call failed: {result.Failure.Message}");
            return;
        }

        var reply = result.Value;
        _history.Add(new ConversationMessage { Role = "user", Content = message, Timestamp = DateTimeOffset.UtcNow });
        _history.Add(new ConversationMessage { Role = "assistant", Content = reply.Content, Timestamp = DateTimeOffset.UtcNow });
        if (reply.Messages is { Count: > 0 })
        {
            _history.Clear();
            _history.AddRange(reply.Messages);
        }

        if (reply.UsedTools)
            CliTheme.WriteMuted("  Tools were used to generate this response.");

        CliTheme.WriteAgentReply(session.AgentName, reply.Content);

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
