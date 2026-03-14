namespace Weave.Agents.Models;

[GenerateSerializer]
public sealed record AgentSupervisorState
{
    [Id(0)] public List<string> AgentNames { get; init; } = [];
    [Id(1)] public string WorkspaceId { get; set; } = string.Empty;
}
