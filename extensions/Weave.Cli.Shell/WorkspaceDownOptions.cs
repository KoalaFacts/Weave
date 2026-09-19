namespace Weave.Cli.Shell;

internal sealed record WorkspaceDownOptions(string? Name, string? ManifestPath = null, string? WorkspaceId = null);
