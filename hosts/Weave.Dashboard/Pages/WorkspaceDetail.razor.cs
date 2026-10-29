using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Weave.Dashboard.Api;

namespace Weave.Dashboard.Pages;

public sealed partial class WorkspaceDetail : ComponentBase
{
    [Inject]
    private WeaveApiClient Api { get; set; } = default!;

    [Parameter]
    public string WorkspaceId { get; set; } = "";

    private WorkspaceDto? _workspace;
    private List<AgentDto> _agents = [];
    private List<ToolConnectionDto> _tools = [];
    private bool _loading = true;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _workspace = await Api.GetWorkspaceAsync(WorkspaceId);
            if (_workspace is null)
            {
                _error = $"Workspace '{WorkspaceId}' not found.";
                return;
            }

            _agents = await Api.GetAgentsAsync(WorkspaceId);
            _tools = await Api.GetToolsAsync(WorkspaceId);
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

    private static BadgeColor GetStatusColor(string status) => status switch
    {
        "Running" => BadgeColor.Brand,
        "Stopped" => BadgeColor.Informative,
        "Error" => BadgeColor.Brand,
        _ => BadgeColor.Informative
    };

    private static BadgeColor GetAgentStatusColor(string status) => status switch
    {
        "Active" => BadgeColor.Brand,
        "Idle" => BadgeColor.Informative,
        "Error" => BadgeColor.Brand,
        _ => BadgeColor.Informative
    };

    private static BadgeColor GetToolStatusColor(string status) => status switch
    {
        "Connected" => BadgeColor.Brand,
        "Disconnected" => BadgeColor.Informative,
        "Error" => BadgeColor.Brand,
        _ => BadgeColor.Informative
    };
}
