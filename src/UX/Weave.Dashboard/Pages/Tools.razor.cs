using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Weave.Dashboard.Services;

namespace Weave.Dashboard.Pages;

public sealed partial class Tools : ComponentBase
{
    [Inject]
    private WeaveApiClient Api { get; set; } = default!;

    private List<ToolConnectionDto> _tools = [];
    private bool _loading = true;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var workspaces = await Api.GetWorkspacesAsync();
            foreach (var ws in workspaces)
            {
                var tools = await Api.GetToolsAsync(ws.WorkspaceId);
                _tools.AddRange(tools);
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

    private static Appearance GetStatusAppearance(string status) => status switch
    {
        "Connected" => Appearance.Accent,
        "Disconnected" => Appearance.Neutral,
        "Error" => Appearance.Accent,
        _ => Appearance.Neutral
    };
}
