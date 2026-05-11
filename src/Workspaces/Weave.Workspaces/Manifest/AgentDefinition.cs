namespace Weave.Workspaces.Manifest;

public sealed record AgentDefinition
{
    public required string Model { get; init; }
    public string? Provider { get; init; }
    public string? ApiKeyRef { get; init; }
    public string? BaseUrl { get; init; }
    public string? SystemPromptFile { get; init; }
    public int MaxConcurrentTasks { get; init; } = 1;
    public MemoryConfig? Memory { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public HeartbeatConfig? Heartbeat { get; init; }
    public TargetSelector? Target { get; init; }
}
