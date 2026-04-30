namespace Weave.Workspaces.Models;

public sealed record AgentDefinition
{
    public required string Model { get; init; }
    public string? SystemPromptFile { get; init; }
    public int MaxConcurrentTasks { get; init; } = 1;
    public MemoryConfig? Memory { get; init; }
    public List<string> Tools { get; init; } = [];
    public List<string> Capabilities { get; init; } = [];
    public HeartbeatConfig? Heartbeat { get; init; }
    public TargetSelector? Target { get; init; }
}