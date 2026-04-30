namespace Weave.Cli.Commands;

internal sealed record WorkspaceDownOptions(string? Name, string? ManifestPath = null, string? WorkspaceId = null);
