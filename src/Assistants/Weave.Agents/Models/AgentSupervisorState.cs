namespace Weave.Agents.Models;
public sealed record AgentSupervisorState
{
    public List<string> AgentNames { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;
}
