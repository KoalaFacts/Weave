using Weave.Agents.Grains;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class ToolRegistryGrainStaticMethodTests
{
    // --- MapToToolSpec: type mapping ---

    [Theory]
    [InlineData("mcp", ToolType.Mcp)]
    [InlineData("cli", ToolType.Cli)]
    [InlineData("openapi", ToolType.OpenApi)]
    [InlineData("dapr", ToolType.Dapr)]
    [InlineData("library", ToolType.Library)]
    [InlineData("direct_http", ToolType.DirectHttp)]
    public void MapToToolSpec_AllToolTypes_MapCorrectly(string typeName, ToolType expectedType)
    {
        var definition = new ToolDefinition { Type = typeName };

        var spec = ToolRegistryGrain.MapToToolSpec("my-tool", definition);

        spec.Name.ShouldBe("my-tool");
        spec.Type.ShouldBe(expectedType);
    }

    [Fact]
    public void MapToToolSpec_UnknownType_ThrowsNotSupported()
    {
        var definition = new ToolDefinition { Type = "unknown" };

        Should.Throw<NotSupportedException>(
            () => ToolRegistryGrain.MapToToolSpec("bad", definition));
    }

    [Fact]
    public void MapToToolSpec_McpConfig_Preserved()
    {
        var definition = new ToolDefinition
        {
            Type = "mcp",
            Mcp = new McpConfig { Server = "/usr/bin/tool", Args = ["--verbose"], Env = new() { ["KEY"] = "val" } }
        };

        var spec = ToolRegistryGrain.MapToToolSpec("mcp-tool", definition);

        spec.Mcp.ShouldNotBeNull();
        spec.Mcp!.Server.ShouldBe("/usr/bin/tool");
        spec.Mcp.Args.ShouldContain("--verbose");
    }

    [Fact]
    public void MapToToolSpec_CliConfig_Preserved()
    {
        var definition = new ToolDefinition
        {
            Type = "cli",
            Cli = new CliConfig { Shell = "/bin/zsh", AllowedCommands = ["git *"] }
        };

        var spec = ToolRegistryGrain.MapToToolSpec("cli-tool", definition);

        spec.Cli.ShouldNotBeNull();
        spec.Cli!.Shell.ShouldBe("/bin/zsh");
    }

    [Fact]
    public void MapToToolSpec_DirectHttpWithAuth_BuildsAuthHeader()
    {
        var definition = new ToolDefinition
        {
            Type = "direct_http",
            DirectHttp = new DirectHttpConfig
            {
                BaseUrl = "http://api.local",
                Auth = new AuthConfig { Type = "Bearer", Token = "my-token" }
            }
        };

        var spec = ToolRegistryGrain.MapToToolSpec("http-tool", definition);

        spec.DirectHttp.ShouldNotBeNull();
        spec.DirectHttp!.BaseUrl.ShouldBe("http://api.local");
        spec.DirectHttp.AuthHeader.ShouldBe("Bearer my-token");
    }

    [Fact]
    public void MapToToolSpec_DirectHttpWithoutAuth_NullAuthHeader()
    {
        var definition = new ToolDefinition
        {
            Type = "direct_http",
            DirectHttp = new DirectHttpConfig { BaseUrl = "http://api.local" }
        };

        var spec = ToolRegistryGrain.MapToToolSpec("http-tool", definition);

        spec.DirectHttp.ShouldNotBeNull();
        spec.DirectHttp!.AuthHeader.ShouldBeNull();
    }

    [Fact]
    public void MapToToolSpec_NullDirectHttpConfig_NullSpec()
    {
        var definition = new ToolDefinition { Type = "direct_http" };

        var spec = ToolRegistryGrain.MapToToolSpec("http-tool", definition);

        spec.DirectHttp.ShouldBeNull();
    }

    // --- EnumerateSecretPaths ---

    [Fact]
    public void EnumerateSecretPaths_SingleSecret_ExtractsPath()
    {
        var paths = ToolRegistryGrain.EnumerateSecretPaths("token={secret:api-key}").ToList();

        paths.ShouldHaveSingleItem();
        paths[0].ShouldBe("api-key");
    }

    [Fact]
    public void EnumerateSecretPaths_MultipleSecrets_ExtractsAll()
    {
        var paths = ToolRegistryGrain.EnumerateSecretPaths(
            "user={secret:db-user}&pass={secret:db-pass}").ToList();

        paths.Count.ShouldBe(2);
        paths.ShouldContain("db-user");
        paths.ShouldContain("db-pass");
    }

    [Fact]
    public void EnumerateSecretPaths_NoSecrets_ReturnsEmpty()
    {
        var paths = ToolRegistryGrain.EnumerateSecretPaths("plain text").ToList();

        paths.ShouldBeEmpty();
    }

    [Fact]
    public void EnumerateSecretPaths_EmptyString_ReturnsEmpty()
    {
        ToolRegistryGrain.EnumerateSecretPaths("").ToList().ShouldBeEmpty();
    }

    [Fact]
    public void EnumerateSecretPaths_MissingCloseBrace_StopsEnumeration()
    {
        var paths = ToolRegistryGrain.EnumerateSecretPaths("{secret:unclosed").ToList();

        paths.ShouldBeEmpty();
    }

    [Fact]
    public void EnumerateSecretPaths_PathWithSlashes_PreservesFullPath()
    {
        var paths = ToolRegistryGrain.EnumerateSecretPaths("{secret:vault/db/password}").ToList();

        paths.ShouldHaveSingleItem();
        paths[0].ShouldBe("vault/db/password");
    }

    [Fact]
    public void EnumerateSecretPaths_AdjacentSecrets_ExtractsBoth()
    {
        var paths = ToolRegistryGrain.EnumerateSecretPaths("{secret:a}{secret:b}").ToList();

        paths.Count.ShouldBe(2);
        paths[0].ShouldBe("a");
        paths[1].ShouldBe("b");
    }

    // --- ResolveEndpoint ---

    [Fact]
    public void ResolveEndpoint_McpType_ReturnsServer()
    {
        var def = new ToolDefinition
        {
            Type = "mcp",
            Mcp = new McpConfig { Server = "/usr/bin/mcp-server" }
        };

        ToolRegistryGrain.ResolveEndpoint(def).ShouldBe("/usr/bin/mcp-server");
    }

    [Fact]
    public void ResolveEndpoint_OpenApiType_ReturnsSpecUrl()
    {
        var def = new ToolDefinition
        {
            Type = "openapi",
            OpenApi = new OpenApiConfig { SpecUrl = "http://api.local/openapi.json" }
        };

        ToolRegistryGrain.ResolveEndpoint(def).ShouldBe("http://api.local/openapi.json");
    }

    [Fact]
    public void ResolveEndpoint_DirectHttpType_ReturnsBaseUrl()
    {
        var def = new ToolDefinition
        {
            Type = "direct_http",
            DirectHttp = new DirectHttpConfig { BaseUrl = "http://api.local:8080" }
        };

        ToolRegistryGrain.ResolveEndpoint(def).ShouldBe("http://api.local:8080");
    }

    [Fact]
    public void ResolveEndpoint_CliType_ReturnsNull()
    {
        var def = new ToolDefinition { Type = "cli" };

        ToolRegistryGrain.ResolveEndpoint(def).ShouldBeNull();
    }

    [Fact]
    public void ResolveEndpoint_DaprType_ReturnsNull()
    {
        var def = new ToolDefinition { Type = "dapr" };

        ToolRegistryGrain.ResolveEndpoint(def).ShouldBeNull();
    }

    [Fact]
    public void ResolveEndpoint_NullConfig_ReturnsNull()
    {
        var def = new ToolDefinition { Type = "mcp" };

        ToolRegistryGrain.ResolveEndpoint(def).ShouldBeNull();
    }
}
