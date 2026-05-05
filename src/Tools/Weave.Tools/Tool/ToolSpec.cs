using Weave.Tools.Connectors;

namespace Weave.Tools.Tool;

public sealed record ToolSpec
{
    public string Name { get; init; } = string.Empty;
    public ToolType Type { get; init; }
    public Weave.Workspaces.Manifest.McpConfig? Mcp { get; init; }
    public DaprToolConfig? Dapr { get; init; }
    public Weave.Workspaces.Manifest.OpenApiConfig? OpenApi { get; init; }
    public Weave.Workspaces.Manifest.CliConfig? Cli { get; init; }
    public DirectHttpToolConfig? DirectHttp { get; init; }
    public FileSystemToolConfig? FileSystem { get; init; }
}
