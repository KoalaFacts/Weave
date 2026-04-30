using Weave.Workspaces.Models;

namespace Weave.Silo.Api;

public sealed record ActivateAgentRequest
{
    public required AgentDefinition Definition { get; init; }
}
