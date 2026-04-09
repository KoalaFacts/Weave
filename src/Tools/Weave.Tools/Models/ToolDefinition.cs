namespace Weave.Tools.Models;

public enum ToolType
{
    Mcp,
    Dapr,
    OpenApi,
    Cli,
    Library,
    DirectHttp,
    FileSystem
}

[GenerateSerializer]
public sealed record ToolSpec
{
    [Id(0)] public string Name { get; init; } = string.Empty;
    [Id(1)] public ToolType Type { get; init; }
    [Id(2)] public Weave.Workspaces.Models.McpConfig? Mcp { get; init; }
    [Id(3)] public DaprToolConfig? Dapr { get; init; }
    [Id(4)] public Weave.Workspaces.Models.OpenApiConfig? OpenApi { get; init; }
    [Id(5)] public Weave.Workspaces.Models.CliConfig? Cli { get; init; }
    [Id(6)] public DirectHttpToolConfig? DirectHttp { get; init; }
    [Id(7)] public FileSystemToolConfig? FileSystem { get; init; }
}

[GenerateSerializer]
public sealed record DaprToolConfig
{
    [Id(0)] public string AppId { get; init; } = string.Empty;
    [Id(1)] public string MethodName { get; init; } = string.Empty;
}

[GenerateSerializer]
public sealed record DirectHttpToolConfig
{
    [Id(0)] public string BaseUrl { get; init; } = string.Empty;
    [Id(1)] public string? AuthHeader { get; init; }
}

[GenerateSerializer]
public sealed record FileSystemToolConfig
{
    [Id(0)] public required string Root { get; init; }
    [Id(1)] public bool ReadOnly { get; init; }
    [Id(2)] public long MaxReadBytes { get; init; }
    [Id(3)] public bool Sandbox { get; init; } = true;
}
