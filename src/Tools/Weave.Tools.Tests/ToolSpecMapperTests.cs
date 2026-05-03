using Weave.Tools.Mapping;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Tools.Tests;

public sealed class ToolSpecMapperTests
{
    [Theory]
    [InlineData("mcp", ToolType.Mcp)]
    [InlineData("cli", ToolType.Cli)]
    [InlineData("openapi", ToolType.OpenApi)]
    [InlineData("dapr", ToolType.Dapr)]
    [InlineData("library", ToolType.Library)]
    [InlineData("direct_http", ToolType.DirectHttp)]
    [InlineData("filesystem", ToolType.FileSystem)]
    public void FromDefinition_AllToolTypes_MapCorrectly(string typeString, ToolType expectedType)
    {
        var definition = new ToolDefinition { Type = typeString };

        var spec = ToolSpecMapper.FromDefinition("test-tool", definition);

        spec.Name.ShouldBe("test-tool");
        spec.Type.ShouldBe(expectedType);
    }

    [Fact]
    public void FromDefinition_UnknownType_ThrowsNotSupported()
    {
        var definition = new ToolDefinition { Type = "unknown" };

        Should.Throw<NotSupportedException>(() =>
            ToolSpecMapper.FromDefinition("test-tool", definition));
    }

    [Fact]
    public void FromDefinition_McpConfig_Preserved()
    {
        var definition = new ToolDefinition
        {
            Type = "mcp",
            Mcp = new McpConfig { Server = "npx", Args = ["@modelcontextprotocol/server"] }
        };

        var spec = ToolSpecMapper.FromDefinition("mcp-tool", definition);

        spec.Mcp.ShouldNotBeNull();
        spec.Mcp.Server.ShouldBe("npx");
        spec.Mcp.Args.ShouldBe(["@modelcontextprotocol/server"]);
    }

    [Fact]
    public void FromDefinition_CliConfig_Preserved()
    {
        var definition = new ToolDefinition
        {
            Type = "cli",
            Cli = new CliConfig { Shell = "/bin/zsh" }
        };

        var spec = ToolSpecMapper.FromDefinition("cli-tool", definition);

        spec.Cli.ShouldNotBeNull();
        spec.Cli.Shell.ShouldBe("/bin/zsh");
    }

    [Fact]
    public void FromDefinition_DirectHttpWithAuth_BuildsAuthHeader()
    {
        var definition = new ToolDefinition
        {
            Type = "direct_http",
            DirectHttp = new DirectHttpConfig
            {
                BaseUrl = "https://api.example.com",
                Auth = new AuthConfig { Type = "Bearer", Token = "my-token" }
            }
        };

        var spec = ToolSpecMapper.FromDefinition("http-tool", definition);

        spec.DirectHttp.ShouldNotBeNull();
        spec.DirectHttp.AuthHeader.ShouldBe("Bearer my-token");
    }

    [Fact]
    public void FromDefinition_DirectHttpWithoutAuth_NullAuthHeader()
    {
        var definition = new ToolDefinition
        {
            Type = "direct_http",
            DirectHttp = new DirectHttpConfig { BaseUrl = "https://api.example.com" }
        };

        var spec = ToolSpecMapper.FromDefinition("http-tool", definition);

        spec.DirectHttp.ShouldNotBeNull();
        spec.DirectHttp.AuthHeader.ShouldBeNull();
    }

    [Fact]
    public void FromDefinition_NullDirectHttpConfig_NullSpec()
    {
        var definition = new ToolDefinition { Type = "direct_http" };

        var spec = ToolSpecMapper.FromDefinition("http-tool", definition);

        spec.DirectHttp.ShouldBeNull();
    }

    [Fact]
    public void FromDefinition_FileSystemConfig_Preserved()
    {
        var definition = new ToolDefinition
        {
            Type = "filesystem",
            FileSystem = new Weave.Workspaces.Models.FileSystemToolConfig { Root = "/data/workspace", ReadOnly = true, MaxReadBytes = 512 }
        };

        var spec = ToolSpecMapper.FromDefinition("fs-tool", definition);

        spec.FileSystem.ShouldNotBeNull();
        spec.FileSystem.Root.ShouldBe("/data/workspace");
        spec.FileSystem.ReadOnly.ShouldBeTrue();
        spec.FileSystem.MaxReadBytes.ShouldBe(512);
    }

    [Fact]
    public void ResolveEndpoint_FileSystemType_ReturnsRoot()
    {
        var definition = new ToolDefinition
        {
            Type = "filesystem",
            FileSystem = new Weave.Workspaces.Models.FileSystemToolConfig { Root = "/data/workspace" }
        };

        ToolSpecMapper.ResolveEndpoint(definition).ShouldBe("/data/workspace");
    }

    [Fact]
    public void FromDefinition_NullFileSystemToolConfig_NullSpec()
    {
        var definition = new ToolDefinition { Type = "filesystem" };

        var spec = ToolSpecMapper.FromDefinition("fs-tool", definition);

        spec.FileSystem.ShouldBeNull();
    }

    [Fact]
    public void ResolveEndpoint_McpType_ReturnsServer()
    {
        var definition = new ToolDefinition
        {
            Type = "mcp",
            Mcp = new McpConfig { Server = "npx" }
        };

        ToolSpecMapper.ResolveEndpoint(definition).ShouldBe("npx");
    }

    [Fact]
    public void ResolveEndpoint_OpenApiType_ReturnsSpecUrl()
    {
        var definition = new ToolDefinition
        {
            Type = "openapi",
            OpenApi = new OpenApiConfig { SpecUrl = "https://api.example.com/spec.json" }
        };

        ToolSpecMapper.ResolveEndpoint(definition).ShouldBe("https://api.example.com/spec.json");
    }

    [Fact]
    public void ResolveEndpoint_DirectHttpType_ReturnsBaseUrl()
    {
        var definition = new ToolDefinition
        {
            Type = "direct_http",
            DirectHttp = new DirectHttpConfig { BaseUrl = "https://api.example.com" }
        };

        ToolSpecMapper.ResolveEndpoint(definition).ShouldBe("https://api.example.com");
    }

    [Fact]
    public void ResolveEndpoint_CliType_ReturnsNull()
    {
        var definition = new ToolDefinition { Type = "cli" };

        ToolSpecMapper.ResolveEndpoint(definition).ShouldBeNull();
    }

    [Fact]
    public void ResolveEndpoint_DaprType_ReturnsNull()
    {
        var definition = new ToolDefinition { Type = "dapr" };

        ToolSpecMapper.ResolveEndpoint(definition).ShouldBeNull();
    }

    [Fact]
    public void ResolveEndpoint_NullConfig_ReturnsNull()
    {
        var definition = new ToolDefinition { Type = "mcp" };

        ToolSpecMapper.ResolveEndpoint(definition).ShouldBeNull();
    }
}
