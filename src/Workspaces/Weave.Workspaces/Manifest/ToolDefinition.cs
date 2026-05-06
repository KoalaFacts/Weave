namespace Weave.Workspaces.Manifest;

public sealed record ToolDefinition
{
    public required string Type { get; init; }
    public string? Version { get; init; }
    public McpConfig? Mcp { get; init; }
    public OpenApiConfig? OpenApi { get; init; }
    public CliConfig? Cli { get; init; }
    public DirectHttpConfig? DirectHttp { get; init; }
    public FileSystemToolConfig? FileSystem { get; init; }
}
