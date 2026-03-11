using System.Diagnostics.CodeAnalysis;
using Weave.Workspaces.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Weave.Workspaces.Manifest;

public interface IManifestParser
{
    WorkspaceManifest Parse(string yaml);
    WorkspaceManifest ParseFile(string path);
    string Serialize(WorkspaceManifest manifest);
    IReadOnlyList<string> Validate(WorkspaceManifest manifest);
}

[RequiresDynamicCode("ManifestParser uses YamlDotNet reflection-based serialization.")]
public sealed class ManifestParser : IManifestParser
{
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;

    public ManifestParser()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
            .Build();
    }

    public WorkspaceManifest Parse(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        var dto = _deserializer.Deserialize<ManifestDto>(yaml);
        return MapToManifest(dto);
    }

    public WorkspaceManifest ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var yaml = File.ReadAllText(path);
        return Parse(yaml);
    }

    public string Serialize(WorkspaceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var dto = MapToDto(manifest);
        return _serializer.Serialize(dto);
    }

    public IReadOnlyList<string> Validate(WorkspaceManifest manifest)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.Version))
            errors.Add("'version' is required.");

        if (string.IsNullOrWhiteSpace(manifest.Name))
            errors.Add("'name' is required.");

        if (manifest.Version is not "1.0")
            errors.Add($"Unsupported manifest version '{manifest.Version}'. Expected '1.0'.");

        foreach (var (agentName, agent) in manifest.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Model))
                errors.Add($"Agent '{agentName}': 'model' is required.");

            foreach (var toolRef in agent.Tools)
            {
                if (!manifest.Tools.ContainsKey(toolRef))
                    errors.Add($"Agent '{agentName}' references undefined tool '{toolRef}'.");
            }

            if (agent.Heartbeat is { } hb && string.IsNullOrWhiteSpace(hb.Cron))
                errors.Add($"Agent '{agentName}': heartbeat 'cron' is required when heartbeat is configured.");
        }

        foreach (var (toolName, tool) in manifest.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Type))
                errors.Add($"Tool '{toolName}': 'type' is required.");

            var validTypes = new[] { "mcp", "dapr", "openapi", "cli", "library" };
            if (!validTypes.Contains(tool.Type))
                errors.Add($"Tool '{toolName}': invalid type '{tool.Type}'. Must be one of: {string.Join(", ", validTypes)}.");
        }

        foreach (var (targetName, target) in manifest.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Runtime))
                errors.Add($"Target '{targetName}': 'runtime' is required.");
        }

        return errors;
    }

    private static WorkspaceManifest MapToManifest(ManifestDto dto)
    {
        return new WorkspaceManifest
        {
            Version = dto.Version ?? "1.0",
            Name = dto.Name ?? "unnamed",
            Workspace = MapWorkspaceConfig(dto.Workspace),
            Agents = dto.Agents?.ToDictionary(
                kvp => kvp.Key,
                kvp => MapAgentDefinition(kvp.Value)) ?? [],
            Tools = dto.Tools?.ToDictionary(
                kvp => kvp.Key,
                kvp => MapToolDefinition(kvp.Value)) ?? [],
            Targets = dto.Targets?.ToDictionary(
                kvp => kvp.Key,
                kvp => MapTargetDefinition(kvp.Value)) ?? [],
            Hooks = dto.Hooks is not null ? MapHooksConfig(dto.Hooks) : null
        };
    }

    private static WorkspaceConfig MapWorkspaceConfig(WorkspaceConfigDto? dto)
    {
        if (dto is null) return new WorkspaceConfig();
        return new WorkspaceConfig
        {
            Isolation = Enum.TryParse<IsolationLevel>(dto.Isolation, true, out var level) ? level : IsolationLevel.Full,
            Network = dto.Network is not null ? new NetworkConfig { Name = dto.Network.Name, Subnet = dto.Network.Subnet } : null,
            Filesystem = dto.Filesystem is not null ? new FilesystemConfig
            {
                Root = dto.Filesystem.Root,
                Mounts = dto.Filesystem.Mounts?.Select(m => new MountConfig
                {
                    Source = m.Source ?? "",
                    Target = m.Target ?? "",
                    Readonly = m.Readonly
                }).ToList() ?? []
            } : null,
            Secrets = dto.Secrets is not null ? new SecretsConfig
            {
                Provider = dto.Secrets.Provider ?? "env",
                Vault = dto.Secrets.Vault is not null ? new VaultConfig
                {
                    Address = dto.Secrets.Vault.Address,
                    Mount = dto.Secrets.Vault.Mount
                } : null
            } : null
        };
    }

    private static AgentDefinition MapAgentDefinition(AgentDefinitionDto dto) => new()
    {
        Model = dto.Model ?? "",
        SystemPromptFile = dto.SystemPromptFile,
        MaxConcurrentTasks = dto.MaxConcurrentTasks,
        Memory = dto.Memory is not null ? new MemoryConfig { Provider = dto.Memory.Provider ?? "in-memory", Ttl = dto.Memory.Ttl } : null,
        Tools = dto.Tools ?? [],
        Capabilities = dto.Capabilities ?? [],
        Heartbeat = dto.Heartbeat is not null ? new HeartbeatConfig { Cron = dto.Heartbeat.Cron ?? "", Tasks = dto.Heartbeat.Tasks ?? [] } : null,
        Target = dto.Target is not null ? new TargetSelector { Labels = dto.Target.Labels ?? [] } : null
    };

    private static ToolDefinition MapToolDefinition(ToolDefinitionDto dto) => new()
    {
        Type = dto.Type ?? "",
        Mcp = dto.Mcp is not null ? new McpConfig { Server = dto.Mcp.Server ?? "", Args = dto.Mcp.Args ?? [], Env = dto.Mcp.Env ?? [] } : null,
        OpenApi = dto.OpenApi is not null ? new OpenApiConfig
        {
            SpecUrl = dto.OpenApi.SpecUrl ?? "",
            Auth = dto.OpenApi.Auth is not null ? new AuthConfig { Type = dto.OpenApi.Auth.Type ?? "", Token = dto.OpenApi.Auth.Token } : null
        } : null,
        Cli = dto.Cli is not null ? new CliConfig { Shell = dto.Cli.Shell ?? "/bin/bash", AllowedCommands = dto.Cli.AllowedCommands ?? [], DeniedCommands = dto.Cli.DeniedCommands ?? [] } : null
    };

    private static TargetDefinition MapTargetDefinition(TargetDefinitionDto dto) => new()
    {
        Runtime = dto.Runtime ?? "",
        Replicas = dto.Replicas,
        Trigger = dto.Trigger,
        Region = dto.Region,
        Scaling = dto.Scaling is not null ? new ScalingConfig { Min = dto.Scaling.Min, Max = dto.Scaling.Max } : null
    };

    private static HooksConfig MapHooksConfig(HooksConfigDto dto) => new()
    {
        Workspace = dto.Workspace is not null ? new WorkspaceHooks
        {
            PreStart = dto.Workspace.PreStart ?? [],
            PostStart = dto.Workspace.PostStart ?? [],
            PreStop = dto.Workspace.PreStop ?? [],
            PostStop = dto.Workspace.PostStop ?? []
        } : null,
        Agents = dto.Agents?.ToDictionary(
            kvp => kvp.Key,
            kvp => new AgentHooks
            {
                OnActivated = kvp.Value.OnActivated ?? [],
                OnDeactivated = kvp.Value.OnDeactivated ?? [],
                OnError = kvp.Value.OnError ?? []
            }),
        Tools = dto.Tools?.ToDictionary(
            kvp => kvp.Key,
            kvp => new ToolHooks
            {
                OnConnected = kvp.Value.OnConnected ?? [],
                OnDisconnected = kvp.Value.OnDisconnected ?? [],
                OnError = kvp.Value.OnError ?? []
            })
    };

    private static ManifestDto MapToDto(WorkspaceManifest manifest) => new()
    {
        Version = manifest.Version,
        Name = manifest.Name
    };
}

// YamlDotNet DTOs (snake_case mapping)
#pragma warning disable CA1716, CA1002, CA1819

internal sealed class ManifestDto
{
    public string? Version { get; set; }
    public string? Name { get; set; }
    public WorkspaceConfigDto? Workspace { get; set; }
    public Dictionary<string, AgentDefinitionDto>? Agents { get; set; }
    public Dictionary<string, ToolDefinitionDto>? Tools { get; set; }
    public Dictionary<string, TargetDefinitionDto>? Targets { get; set; }
    public HooksConfigDto? Hooks { get; set; }
}

internal sealed class WorkspaceConfigDto
{
    public string? Isolation { get; set; }
    public NetworkConfigDto? Network { get; set; }
    public FilesystemConfigDto? Filesystem { get; set; }
    public SecretsConfigDto? Secrets { get; set; }
}

internal sealed class NetworkConfigDto
{
    public string? Name { get; set; }
    public string? Subnet { get; set; }
}

internal sealed class FilesystemConfigDto
{
    public string? Root { get; set; }
    public List<MountConfigDto>? Mounts { get; set; }
}

internal sealed class MountConfigDto
{
    public string? Source { get; set; }
    public string? Target { get; set; }
    public bool Readonly { get; set; }
}

internal sealed class SecretsConfigDto
{
    public string? Provider { get; set; }
    public VaultConfigDto? Vault { get; set; }
}

internal sealed class VaultConfigDto
{
    public string? Address { get; set; }
    public string? Mount { get; set; }
}

internal sealed class AgentDefinitionDto
{
    public string? Model { get; set; }
    public string? SystemPromptFile { get; set; }
    public int MaxConcurrentTasks { get; set; } = 1;
    public MemoryConfigDto? Memory { get; set; }
    public List<string>? Tools { get; set; }
    public List<string>? Capabilities { get; set; }
    public HeartbeatConfigDto? Heartbeat { get; set; }
    public TargetSelectorDto? Target { get; set; }
}

internal sealed class MemoryConfigDto
{
    public string? Provider { get; set; }
    public string? Ttl { get; set; }
}

internal sealed class HeartbeatConfigDto
{
    public string? Cron { get; set; }
    public List<string>? Tasks { get; set; }
}

internal sealed class TargetSelectorDto
{
    public List<string>? Labels { get; set; }
}

internal sealed class ToolDefinitionDto
{
    public string? Type { get; set; }
    public McpConfigDto? Mcp { get; set; }
    public OpenApiConfigDto? OpenApi { get; set; }
    public CliConfigDto? Cli { get; set; }
}

internal sealed class McpConfigDto
{
    public string? Server { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}

internal sealed class OpenApiConfigDto
{
    public string? SpecUrl { get; set; }
    public AuthConfigDto? Auth { get; set; }
}

internal sealed class AuthConfigDto
{
    public string? Type { get; set; }
    public string? Token { get; set; }
}

internal sealed class CliConfigDto
{
    public string? Shell { get; set; }
    public List<string>? AllowedCommands { get; set; }
    public List<string>? DeniedCommands { get; set; }
}

internal sealed class TargetDefinitionDto
{
    public string? Runtime { get; set; }
    public int Replicas { get; set; } = 1;
    public string? Trigger { get; set; }
    public string? Region { get; set; }
    public ScalingConfigDto? Scaling { get; set; }
}

internal sealed class ScalingConfigDto
{
    public int Min { get; set; } = 1;
    public int Max { get; set; } = 1;
}

internal sealed class HooksConfigDto
{
    public WorkspaceHooksDto? Workspace { get; set; }
    public Dictionary<string, AgentHooksDto>? Agents { get; set; }
    public Dictionary<string, ToolHooksDto>? Tools { get; set; }
}

internal sealed class WorkspaceHooksDto
{
    public List<string>? PreStart { get; set; }
    public List<string>? PostStart { get; set; }
    public List<string>? PreStop { get; set; }
    public List<string>? PostStop { get; set; }
}

internal sealed class AgentHooksDto
{
    public List<string>? OnActivated { get; set; }
    public List<string>? OnDeactivated { get; set; }
    public List<string>? OnError { get; set; }
}

internal sealed class ToolHooksDto
{
    public List<string>? OnConnected { get; set; }
    public List<string>? OnDisconnected { get; set; }
    public List<string>? OnError { get; set; }
}

#pragma warning restore CA1716, CA1002, CA1819
