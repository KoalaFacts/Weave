namespace Weave.Actions.Agent;

/// <summary>
/// Frontend-friendly view of a live agent returned by <see cref="ISiloApi.ListAgentsAsync"/>.
/// Translated from the CLI's internal API DTO at the silo-client boundary so
/// the action layer stays oblivious to the wire shape.
/// </summary>
public sealed record AgentSummary
{
    public required string AgentName { get; init; }
    public required string Status { get; init; }
    public string? Model { get; init; }
    public int ActiveTasksCount { get; init; }
    public int ConnectedToolsCount { get; init; }
}
