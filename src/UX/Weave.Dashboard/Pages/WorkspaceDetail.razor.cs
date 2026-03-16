using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Weave.Dashboard.Services;

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

    private static Appearance GetStatusAppearance(string status) => status switch
    {
        "Running" => Appearance.Accent,
        "Stopped" => Appearance.Neutral,
        "Error" => Appearance.Accent,
        _ => Appearance.Neutral
    };

    private static Appearance GetAgentStatusAppearance(string status) => status switch
    {
        "Active" => Appearance.Accent,
        "Idle" => Appearance.Neutral,
        "Error" => Appearance.Accent,
        _ => Appearance.Neutral
    };

    private static Appearance GetToolStatusAppearance(string status) => status switch
    {
        "Connected" => Appearance.Accent,
        "Disconnected" => Appearance.Neutral,
        "Error" => Appearance.Accent,
        _ => Appearance.Neutral
    };
}
