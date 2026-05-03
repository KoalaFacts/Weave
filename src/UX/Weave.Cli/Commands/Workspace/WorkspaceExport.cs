using System.Text.Json;

namespace Weave.Cli.Commands;

internal sealed record WorkspaceExport
{
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    public string WeaveVersion { get; init; } = "1.0";
    public string WorkspaceName { get; init; } = string.Empty;
    public string? WorkspaceId { get; set; }
    public string? Manifest { get; set; }
    public Dictionary<string, string> PromptFiles { get; init; } = [];
    public IReadOnlyList<ApiAgentResponse> Agents { get; set; } = [];
    public IReadOnlyList<ApiToolResponse> Tools { get; set; } = [];
    public IReadOnlyList<JsonElement> Skills { get; set; } = [];
    public IReadOnlyList<JsonElement> Channels { get; set; } = [];
    public IReadOnlyList<ApiMarketplaceItemResponse> MarketplaceItems { get; set; } = [];
    public IReadOnlyList<JsonElement> Templates { get; set; } = [];
}
