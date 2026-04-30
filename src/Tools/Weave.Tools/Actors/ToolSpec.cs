namespace Weave.Tools.Models;

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