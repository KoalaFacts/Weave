namespace Weave.Workspaces.Models;

public sealed record WorkspaceManifest
{
    public required string Version { get; init; }
    public required string Name { get; init; }
    public WorkspaceConfig Workspace { get; init; } = new();
    public Dictionary<string, AgentDefinition> Agents { get; init; } = [];
    public Dictionary<string, ToolDefinition> Tools { get; init; } = [];
    public Dictionary<string, TargetDefinition> Targets { get; init; } = [];
    public HooksConfig? Hooks { get; init; }
    public Dictionary<string, PluginDefinition> Plugins { get; init; } = [];
    public Dictionary<string, ChannelDefinition> Channels { get; init; } = [];
}
public sealed record WorkspaceConfig
{
    public IsolationLevel Isolation { get; init; } = IsolationLevel.Full;
    public NetworkConfig? Network { get; init; }
    public FilesystemConfig? Filesystem { get; init; }
    public SecretsConfig? Secrets { get; init; }
    public StorageConfig? Storage { get; init; }
}
public sealed record StorageConfig
{
    public required string Backend { get; init; }
    public string? ConnectionString { get; init; }
    public string? Schema { get; init; }
    public StorageIsolation Isolation { get; init; } = StorageIsolation.Database;
    public string? Database { get; init; }
}

public enum StorageIsolation
{
    Schema,
    Database
}
public sealed record NetworkConfig
{
    public string? Name { get; init; }
    public string? Subnet { get; init; }
}
public sealed record FilesystemConfig
{
    public string? Root { get; init; }
    public List<MountConfig> Mounts { get; init; } = [];
}
public sealed record MountConfig
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool Readonly { get; init; }
}
public sealed record SecretsConfig
{
    public string Provider { get; init; } = "env";
    public VaultConfig? Vault { get; init; }
}
public sealed record VaultConfig
{
    public string? Address { get; init; }
    public string? Mount { get; init; }
}
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
public sealed record MemoryConfig
{
    public string Provider { get; init; } = "in-memory";
    public string? Ttl { get; init; }
}
public sealed record HeartbeatConfig
{
    public required string Cron { get; init; }
    public List<string> Tasks { get; init; } = [];
}
public sealed record TargetSelector
{
    public List<string> Labels { get; init; } = [];
}
public sealed record ToolDefinition
{
    public required string Type { get; init; }
    public McpConfig? Mcp { get; init; }
    public OpenApiConfig? OpenApi { get; init; }
    public CliConfig? Cli { get; init; }
    public DirectHttpConfig? DirectHttp { get; init; }
    public FileSystemToolConfig? FileSystem { get; init; }
}
public sealed record McpConfig
{
    public required string Server { get; init; }
    public List<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = [];
}
public sealed record OpenApiConfig
{
    public required string SpecUrl { get; init; }
    public AuthConfig? Auth { get; init; }
}
public sealed record AuthConfig
{
    public required string Type { get; init; }
    public string? Token { get; init; }
}
public sealed record CliConfig
{
    public string Shell { get; init; } = "/bin/bash";
    public List<string> AllowedCommands { get; init; } = [];
    public List<string> DeniedCommands { get; init; } = [];
}
public sealed record DirectHttpConfig
{
    public required string BaseUrl { get; init; }
    public AuthConfig? Auth { get; init; }
}
public sealed record FileSystemToolConfig
{
    public required string Root { get; init; }
    public bool ReadOnly { get; init; }
    public long MaxReadBytes { get; init; }
    public bool Sandbox { get; init; } = true;
}
public sealed record TargetDefinition
{
    public required string Runtime { get; init; }
    public int Replicas { get; init; } = 1;
    public string? Trigger { get; init; }
    public string? Region { get; init; }
    public ScalingConfig? Scaling { get; init; }
}
public sealed record ScalingConfig
{
    public int Min { get; init; } = 1;
    public int Max { get; init; } = 1;
}
public sealed record PluginDefinition
{
    public required string Type { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string> Config { get; init; } = [];
    public string? EnabledWhen { get; init; }
}
public sealed record HooksConfig
{
    public WorkspaceHooks? Workspace { get; init; }
    public Dictionary<string, AgentHooks>? Agents { get; init; }
    public Dictionary<string, ToolHooks>? Tools { get; init; }
}
public sealed record WorkspaceHooks
{
    public List<string> PreStart { get; init; } = [];
    public List<string> PostStart { get; init; } = [];
    public List<string> PreStop { get; init; } = [];
    public List<string> PostStop { get; init; } = [];
}
public sealed record AgentHooks
{
    public List<string> OnActivated { get; init; } = [];
    public List<string> OnDeactivated { get; init; } = [];
    public List<string> OnError { get; init; } = [];
}
public sealed record ToolHooks
{
    public List<string> OnConnected { get; init; } = [];
    public List<string> OnDisconnected { get; init; } = [];
    public List<string> OnError { get; init; } = [];
}
public sealed record ChannelDefinition
{
    public required string Type { get; init; }
    public string? TargetAgent { get; init; }
    public Dictionary<string, string> Config { get; init; } = [];
    public bool Enabled { get; init; } = true;
}

public enum IsolationLevel
{
    Full,
    Shared,
    None
}
