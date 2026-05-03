using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal sealed class TuiChatSession
{
    private readonly List<ApiConversationMessage> _history = [];

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

        ApiChatResponse? reply = null;
        Exception? error = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(CliTheme.AccentStyle)
            .StartAsync($"{session.AgentName} is thinking…", async _ =>
            {
                try
                {
                    using var client = new WorkspaceApiClient();
                    reply = await client.SendAgentMessageAsync(
                        session.WorkspaceId!, session.AgentName, message, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
                {
                    error = ex;
                }
            });

        if (error is not null)
        {
            CliTheme.WriteError($"Agent call failed: {error.Message}");
        }
        else if (reply is not null)
        {
            _history.Add(new ApiConversationMessage { Role = "user", Content = message, Timestamp = DateTimeOffset.UtcNow });
            _history.Add(new ApiConversationMessage { Role = "assistant", Content = reply.Content, Timestamp = DateTimeOffset.UtcNow });
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
    }

    private static void RenderHistory(
        TuiSession session,
        List<ApiConversationMessage> conversationHistory)
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
