using Microsoft.AspNetCore.Components;
using Weave.Dashboard.Services;

namespace Weave.Dashboard.Pages;

public sealed partial class Agents : ComponentBase
{
    [Inject]
    private WeaveApiClient Api { get; set; } = default!;

    private sealed record ChatMessage(string Role, string Content);

    private List<AgentDto> _agents = [];
    private AgentDto? _selectedAgent;
    private string _inputMessage = string.Empty;
    private List<ChatMessage> _messages = [];
    private bool _loading = true;
    private bool _sending;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var workspaces = await Api.GetWorkspacesAsync();
            foreach (var ws in workspaces)
            {
                var agents = await Api.GetAgentsAsync(ws.WorkspaceId);
                _agents.AddRange(agents);
            }
        }
        catch (HttpRequestException)
        {
            _error = "Unable to connect to the Weave Silo API.";
        }
        finally
        {
            _loading = false;
        }
    }

    private void SelectAgent(AgentDto agent)
    {
        _selectedAgent = agent;
        _messages = [];
    }

    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_inputMessage) || _selectedAgent is null)
            return;

        var content = _inputMessage;
        _inputMessage = string.Empty;
        _messages.Add(new ChatMessage("You", content));
        _sending = true;

        try
        {
            var response = await Api.SendMessageAsync(
                _selectedAgent.WorkspaceId,
                _selectedAgent.AgentName,
                content);

            if (response is not null)
                _messages.Add(new ChatMessage("Agent", response.Content));
        }
        catch (HttpRequestException)
        {
            _messages.Add(new ChatMessage("System", "Failed to send message."));
        }
        finally
        {
            _sending = false;
        }
    }
}
