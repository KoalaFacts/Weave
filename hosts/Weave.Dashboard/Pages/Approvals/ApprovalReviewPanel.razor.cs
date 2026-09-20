using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Weave.Dashboard.Approvals;

namespace Weave.Dashboard.Pages.Approvals;

public sealed partial class ApprovalReviewPanel : ComponentBase
{
    [Parameter, EditorRequired]
    public ApprovalReviewSnapshot Snapshot { get; set; } = default!;

    [Parameter]
    public bool Expired { get; set; }

    private static string Display(string? value) => value is null ? "null" : "\"" + JsonEncodedText.Encode(value) + "\"";
}
