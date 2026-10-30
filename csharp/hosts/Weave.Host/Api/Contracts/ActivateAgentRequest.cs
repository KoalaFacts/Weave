using Weave.Workspaces.Manifest;
namespace Weave.Silo.Api;

public sealed record ActivateAgentRequest
{
    public required AgentDefinition Definition { get; init; }
}
