using System.Text.Json;

namespace Weave.Cli.Commands;

/// <summary>
/// Snapshot of a workspace, written as JSON for export/import roundtrips.
/// Live data lists are stored as opaque <see cref="JsonElement"/> for wire
/// fidelity — re-import consumes <see cref="Manifest"/>,
/// <see cref="PromptFiles"/>, <see cref="Skills"/>, and
/// <see cref="Channels"/>; <see cref="MarketplaceItems"/> and
/// <see cref="Templates"/> are diagnostic and round-trip unchanged through
/// any future silo schema bump. Agents and tools are runtime state that the
/// silo regenerates from the manifest on re-import, so they're not stored.
/// </summary>
internal sealed record WorkspaceExport
{
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    public string WeaveVersion { get; init; } = "1.0";
    public string WorkspaceName { get; init; } = string.Empty;
    public string? WorkspaceId { get; set; }
    public string? Manifest { get; set; }
    public Dictionary<string, string> PromptFiles { get; init; } = [];
    public IReadOnlyList<JsonElement> Skills { get; set; } = [];
    public IReadOnlyList<JsonElement> Channels { get; set; } = [];
    public IReadOnlyList<JsonElement> MarketplaceItems { get; set; } = [];
    public IReadOnlyList<JsonElement> Templates { get; set; } = [];
}
