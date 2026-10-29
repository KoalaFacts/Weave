using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Weave.Dashboard.Api;

namespace Weave.Dashboard.Pages;

public sealed partial class Workspaces : ComponentBase
{
    [Inject]
    private WeaveApiClient Api { get; set; } = default!;

    private IQueryable<WorkspaceDto> _workspaces = Array.Empty<WorkspaceDto>().AsQueryable();
    private bool _loading = true;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var workspaces = await Api.GetWorkspacesAsync();
            _workspaces = workspaces.AsQueryable();
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
}
