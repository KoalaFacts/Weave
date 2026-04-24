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
public sealed record ToolSpec
{
    public string Name { get; init; } = string.Empty;
    public ToolType Type { get; init; }
    public Weave.Workspaces.Models.McpConfig? Mcp { get; init; }
    public DaprToolConfig? Dapr { get; init; }
    public Weave.Workspaces.Models.OpenApiConfig? OpenApi { get; init; }
    public Weave.Workspaces.Models.CliConfig? Cli { get; init; }
    public DirectHttpToolConfig? DirectHttp { get; init; }
    public FileSystemToolConfig? FileSystem { get; init; }
}
public sealed record DaprToolConfig
{
    public string AppId { get; init; } = string.Empty;
    public string MethodName { get; init; } = string.Empty;
}
public sealed record DirectHttpToolConfig
{
    public string BaseUrl { get; init; } = string.Empty;
    public string? AuthHeader { get; init; }
}
public sealed record FileSystemToolConfig
{
    public required string Root { get; init; }
    public bool ReadOnly { get; init; }
    public long MaxReadBytes { get; init; }
    public bool Sandbox { get; init; } = true;
}
