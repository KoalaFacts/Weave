using Weave.Tools.InstallMcpTool;

namespace Weave.Workspaces.RuntimeRecovery;

public sealed record WorkspaceHostedServices
{
    public required string WorkspaceId { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<string> Plugins { get; init; } = [];
    public IReadOnlyList<McpToolInstallationSnapshot> McpInstallations { get; init; } = [];
}
