using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Weave.Dashboard.Api;

namespace Weave.Dashboard.Pages;

public sealed partial class Audit : ComponentBase
{
    [Inject]
    private WeaveApiClient Api { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Parameter]
    public string? TokenId { get; set; }

    private List<CapabilityAuditEntryDto> _rows = [];
    private bool _loading;
    private string? _error;
    private string? _filterTokenId;

    protected override async Task OnParametersSetAsync()
    {
        _filterTokenId = TokenId;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            _rows = string.IsNullOrWhiteSpace(_filterTokenId)
                ? await Api.GetRecentCapabilityAuditAsync()
                : await Api.GetCapabilityAuditByTokenAsync(_filterTokenId);
        }
        catch (HttpRequestException ex)
        {
            _error = $"Unable to query the Weave Silo audit endpoint: {ex.Message}";
            _rows = [];
        }
        finally
        {
            _loading = false;
        }
    }

    private void ClearFilter()
    {
        _filterTokenId = null;
        Navigation.NavigateTo("/audit");
    }

    private static Appearance GetOutcomeAppearance(string outcome) => outcome switch
    {
        "Allow" => Appearance.Accent,
        "Deny" => Appearance.Accent,
        _ => Appearance.Neutral
    };

    private static string ShortId(string tokenId) =>
        tokenId.Length <= 8 ? tokenId : $"{tokenId[..8]}…";
}
