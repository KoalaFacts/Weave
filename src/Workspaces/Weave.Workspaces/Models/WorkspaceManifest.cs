namespace Weave.Workspaces.Models;

[GenerateSerializer]
public sealed record WorkspaceManifest
{
    [Id(0)] public required string Version { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public WorkspaceConfig Workspace { get; init; } = new();
    [Id(3)] public Dictionary<string, AgentDefinition> Agents { get; init; } = [];
    [Id(4)] public Dictionary<string, ToolDefinition> Tools { get; init; } = [];
    [Id(5)] public Dictionary<string, TargetDefinition> Targets { get; init; } = [];
    [Id(6)] public HooksConfig? Hooks { get; init; }
    [Id(7)] public Dictionary<string, PluginDefinition> Plugins { get; init; } = [];
}

[GenerateSerializer]
public sealed record WorkspaceConfig
{
    [Id(0)] public IsolationLevel Isolation { get; init; } = IsolationLevel.Full;
    [Id(1)] public NetworkConfig? Network { get; init; }
    [Id(2)] public FilesystemConfig? Filesystem { get; init; }
    [Id(3)] public SecretsConfig? Secrets { get; init; }
}

[GenerateSerializer]
public sealed record NetworkConfig
{
    [Id(0)] public string? Name { get; init; }
    [Id(1)] public string? Subnet { get; init; }
}

[GenerateSerializer]
public sealed record FilesystemConfig
{
    [Id(0)] public string? Root { get; init; }
    [Id(1)] public List<MountConfig> Mounts { get; init; } = [];
}

[GenerateSerializer]
public sealed record MountConfig
{
    [Id(0)] public required string Source { get; init; }
    [Id(1)] public required string Target { get; init; }
    [Id(2)] public bool Readonly { get; init; }
}

[GenerateSerializer]
public sealed record SecretsConfig
{
    [Id(0)] public string Provider { get; init; } = "env";
    [Id(1)] public VaultConfig? Vault { get; init; }
}

[GenerateSerializer]
public sealed record VaultConfig
{
    [Id(0)] public string? Address { get; init; }
    [Id(1)] public string? Mount { get; init; }
}

[GenerateSerializer]
public sealed record AgentDefinition
{
    [Id(0)] public required string Model { get; init; }
    [Id(1)] public string? SystemPromptFile { get; init; }
    [Id(2)] public int MaxConcurrentTasks { get; init; } = 1;
    [Id(3)] public MemoryConfig? Memory { get; init; }
    [Id(4)] public List<string> Tools { get; init; } = [];
    [Id(5)] public List<string> Capabilities { get; init; } = [];
    [Id(6)] public HeartbeatConfig? Heartbeat { get; init; }
    [Id(7)] public TargetSelector? Target { get; init; }
}

[GenerateSerializer]
public sealed record MemoryConfig
{
    [Id(0)] public string Provider { get; init; } = "in-memory";
    [Id(1)] public string? Ttl { get; init; }
}

[GenerateSerializer]
public sealed record HeartbeatConfig
{
    [Id(0)] public required string Cron { get; init; }
    [Id(1)] public List<string> Tasks { get; init; } = [];
}

[GenerateSerializer]
public sealed record TargetSelector
{
    [Id(0)] public List<string> Labels { get; init; } = [];
}

[GenerateSerializer]
public sealed record ToolDefinition
{
    [Id(0)] public required string Type { get; init; }
    [Id(1)] public McpConfig? Mcp { get; init; }
    [Id(2)] public OpenApiConfig? OpenApi { get; init; }
    [Id(3)] public CliConfig? Cli { get; init; }
    [Id(4)] public DirectHttpConfig? DirectHttp { get; init; }
}

[GenerateSerializer]
public sealed record McpConfig
{
    [Id(0)] public required string Server { get; init; }
    [Id(1)] public List<string> Args { get; init; } = [];
    [Id(2)] public Dictionary<string, string> Env { get; init; } = [];
}

[GenerateSerializer]
public sealed record OpenApiConfig
{
    [Id(0)] public required string SpecUrl { get; init; }
    [Id(1)] public AuthConfig? Auth { get; init; }
}

[GenerateSerializer]
public sealed record AuthConfig
{
    [Id(0)] public required string Type { get; init; }
    [Id(1)] public string? Token { get; init; }
}

[GenerateSerializer]
public sealed record CliConfig
{
    [Id(0)] public string Shell { get; init; } = "/bin/bash";
    [Id(1)] public List<string> AllowedCommands { get; init; } = [];
    [Id(2)] public List<string> DeniedCommands { get; init; } = [];
}

[GenerateSerializer]
public sealed record DirectHttpConfig
{
    [Id(0)] public required string BaseUrl { get; init; }
    [Id(1)] public AuthConfig? Auth { get; init; }
}

[GenerateSerializer]
public sealed record TargetDefinition
{
    [Id(0)] public required string Runtime { get; init; }
    [Id(1)] public int Replicas { get; init; } = 1;
    [Id(2)] public string? Trigger { get; init; }
    [Id(3)] public string? Region { get; init; }
    [Id(4)] public ScalingConfig? Scaling { get; init; }
}

[GenerateSerializer]
public sealed record ScalingConfig
{
    [Id(0)] public int Min { get; init; } = 1;
    [Id(1)] public int Max { get; init; } = 1;
}

[GenerateSerializer]
public sealed record PluginDefinition
{
    [Id(0)] public required string Type { get; init; }
    [Id(1)] public string? Description { get; init; }
    [Id(2)] public Dictionary<string, string> Config { get; init; } = [];
    [Id(3)] public string? EnabledWhen { get; init; }
}

[GenerateSerializer]
public sealed record HooksConfig
{
    [Id(0)] public WorkspaceHooks? Workspace { get; init; }
    [Id(1)] public Dictionary<string, AgentHooks>? Agents { get; init; }
    [Id(2)] public Dictionary<string, ToolHooks>? Tools { get; init; }
}

[GenerateSerializer]
public sealed record WorkspaceHooks
{
    [Id(0)] public List<string> PreStart { get; init; } = [];
    [Id(1)] public List<string> PostStart { get; init; } = [];
    [Id(2)] public List<string> PreStop { get; init; } = [];
    [Id(3)] public List<string> PostStop { get; init; } = [];
}

[GenerateSerializer]
public sealed record AgentHooks
{
    [Id(0)] public List<string> OnActivated { get; init; } = [];
    [Id(1)] public List<string> OnDeactivated { get; init; } = [];
    [Id(2)] public List<string> OnError { get; init; } = [];
}

[GenerateSerializer]
public sealed record ToolHooks
{
    [Id(0)] public List<string> OnConnected { get; init; } = [];
    [Id(1)] public List<string> OnDisconnected { get; init; } = [];
    [Id(2)] public List<string> OnError { get; init; } = [];
}

public enum IsolationLevel
{
    Full,
    Shared,
    None
}
