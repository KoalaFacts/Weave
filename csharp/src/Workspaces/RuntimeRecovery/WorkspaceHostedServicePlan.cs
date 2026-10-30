using Weave.Tools.InstallMcpTool;

namespace Weave.Workspaces.RuntimeRecovery;

public sealed record WorkspaceHostedServicePlan
{
    public string Digest { get; init; } = string.Empty;
    public string? BlockReason { get; init; } = "hosted-services-require-restoration";
    public IReadOnlyList<string> RequiredGrants { get; init; } = [];
    public IReadOnlyList<McpToolInstallationSnapshot> McpInstallations { get; init; } = [];
}
