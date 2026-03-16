using Microsoft.AspNetCore.Components;
using Weave.Dashboard.Services;

namespace Weave.Dashboard.Pages;

public sealed partial class Home : ComponentBase
{
    [Inject]
    private WeaveApiClient Api { get; set; } = default!;

    private int _workspaceCount;
    private int _agentCount;
    private int _toolCount;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var workspaces = await Api.GetWorkspacesAsync();
            _workspaceCount = workspaces.Count;

            var agentCount = 0;
            var toolCount = 0;
            foreach (var ws in workspaces)
            {
                var agents = await Api.GetAgentsAsync(ws.WorkspaceId);
                agentCount += agents.Count;

                var tools = await Api.GetToolsAsync(ws.WorkspaceId);
                toolCount += tools.Count;
            }

            _agentCount = agentCount;
            _toolCount = toolCount;
        }
        catch (HttpRequestException)
        {
            _error = "Unable to connect to the Weave Silo API.";
        }
    }
}
