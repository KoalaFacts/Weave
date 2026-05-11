using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;
namespace Weave.Tools.Mapping;

internal static class ToolSpecMapper
{
    public static ToolSpec FromDefinition(string toolName, ToolDefinition definition)
    {
        var type = definition.Type.ToLowerInvariant() switch
        {
            "mcp" => ToolType.Mcp,
            "cli" => ToolType.Cli,
            "openapi" => ToolType.OpenApi,
            "dapr" => ToolType.Dapr,
            "library" => ToolType.Library,
            "direct_http" => ToolType.DirectHttp,
            "filesystem" => ToolType.FileSystem,
            _ => throw new NotSupportedException($"Unsupported tool type: '{definition.Type}'")
        };

        return new ToolSpec
        {
            Name = toolName,
            Type = type,
            Mcp = definition.Mcp,
            OpenApi = definition.OpenApi,
            Cli = definition.Cli,
            DirectHttp = MapDirectHttp(definition.DirectHttp),
            FileSystem = MapFileSystem(definition.FileSystem)
        };
    }

    public static string? ResolveEndpoint(ToolDefinition definition)
    {
        return definition.Type.ToLowerInvariant() switch
        {
            "mcp" => definition.Mcp?.Server,
            "openapi" => definition.OpenApi?.SpecUrl,
            "direct_http" => definition.DirectHttp?.BaseUrl,
            "filesystem" => definition.FileSystem?.Root,
            _ => null
        };
    }

    private static DirectHttpToolConfig? MapDirectHttp(DirectHttpConfig? config)
    {
        if (config is null)
            return null;

        return new DirectHttpToolConfig
        {
            BaseUrl = config.BaseUrl,
            AuthHeader = config.Auth is not null
                ? $"{config.Auth.Type} {config.Auth.Token}"
                : null
        };
    }

    private static Weave.Tools.Connectors.FileSystemToolConfig? MapFileSystem(Weave.Workspaces.Manifest.FileSystemToolConfig? config)
    {
        if (config is null)
            return null;

        return new Weave.Tools.Connectors.FileSystemToolConfig
        {
            Root = config.Root,
            ReadOnly = config.ReadOnly,
            MaxReadBytes = config.MaxReadBytes,
            Sandbox = config.Sandbox
        };
    }
}
