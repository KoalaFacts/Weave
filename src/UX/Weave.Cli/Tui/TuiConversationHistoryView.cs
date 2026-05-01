using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from the TUI shell.")]
internal sealed class TuiConversationHistoryView
{
    public void Render(
        TuiSession session,
        IReadOnlyList<ApiConversationMessage> conversationHistory)
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
            var role = msg.Role ?? "unknown";
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                CliTheme.WriteUserEcho(msg.Content ?? "");
            else
                CliTheme.WriteAgentReply(session.AgentName, msg.Content ?? "");
        }
    }
}
