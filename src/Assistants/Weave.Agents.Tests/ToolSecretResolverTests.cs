using Weave.Agents.Actors;
using Weave.Security.Actors;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class ToolSecretResolverTests
{
    private const string TestSigningKey = "test-signing-key-that-is-at-least-32-chars-long";
    private const string Workspace = "ws-resolver";

    private static (ToolSecretResolver Resolver, ISecretProxyActor Proxy) Create()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var proxy = Substitute.For<ISecretProxyActor>();
        proxy.SubstituteAsync(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(proxy);
        var tokens = new CapabilityTokenService(
            Microsoft.Extensions.Options.Options.Create(
                new CapabilityTokenOptions { SigningKey = TestSigningKey }),
            TimeProvider.System);
        return (new ToolSecretResolver(actors, tokens), proxy);
    }

    [Fact]
    public async Task Resolve_DefinitionWithNoSecrets_DoesNotCallProxy()
    {
        var (resolver, proxy) = Create();
        var definition = new ToolDefinition
        {
            Type = "mcp",
            Mcp = new McpConfig { Server = "npx", Env = new Dictionary<string, string> { ["LOG_LEVEL"] = "info" } }
        };

        await resolver.ResolveAsync(Workspace, definition);

        await proxy.DidNotReceive().RegisterSecretAsync(Arg.Any<string>(), Arg.Any<CapabilityToken>());
    }

    [Fact]
    public async Task Resolve_McpEnvSecretRef_MintsTokenLimitedToReferencedPath()
    {
        var (resolver, proxy) = Create();
        var captured = new List<CapabilityToken>();
        proxy
            .RegisterSecretAsync(Arg.Any<string>(), Arg.Do<CapabilityToken>(t => captured.Add(t)))
            .Returns(ci => Task.FromResult($"{{secret:{ci.Arg<string>()}}}"));

        var definition = new ToolDefinition
        {
            Type = "mcp",
            Mcp = new McpConfig
            {
                Server = "npx",
                Env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "{secret:gh-token}" }
            }
        };

        await resolver.ResolveAsync(Workspace, definition);

        captured.Count.ShouldBe(1);
        captured[0].Grants.ShouldBe(["secret:gh-token"]);
        captured[0].Grants.ShouldNotContain("secret:*");
    }

    [Fact]
    public async Task Resolve_OpenApiAndMcpSecretRefs_MintsTokenWithDedupedUnion()
    {
        var (resolver, proxy) = Create();
        var captured = new List<CapabilityToken>();
        proxy
            .RegisterSecretAsync(Arg.Any<string>(), Arg.Do<CapabilityToken>(t => captured.Add(t)))
            .Returns(Task.FromResult("placeholder"));

        var definition = new ToolDefinition
        {
            Type = "openapi",
            Mcp = new McpConfig
            {
                Server = "npx",
                Env = new Dictionary<string, string>
                {
                    ["A"] = "{secret:shared}",
                    ["B"] = "{secret:mcp-only}"
                }
            },
            OpenApi = new OpenApiConfig
            {
                SpecUrl = "https://example/spec",
                Auth = new AuthConfig { Type = "bearer", Token = "{secret:shared}/{secret:openapi-only}" }
            }
        };

        await resolver.ResolveAsync(Workspace, definition);

        // Every captured token must carry the full deduped grant set.
        captured.Count.ShouldBeGreaterThan(0);
        var first = captured[0];
        first.Grants.ShouldBe(["secret:shared", "secret:mcp-only", "secret:openapi-only"], ignoreOrder: true);
        first.Grants.ShouldNotContain("secret:*");

        // Same token instance is reused for every RegisterSecretAsync call in the resolution.
        captured.ShouldAllBe(t => ReferenceEquals(t, first));
    }

    [Fact]
    public async Task Resolve_DoesNotGrantSecretWildcard()
    {
        var (resolver, proxy) = Create();
        var captured = new List<CapabilityToken>();
        proxy
            .RegisterSecretAsync(Arg.Any<string>(), Arg.Do<CapabilityToken>(t => captured.Add(t)))
            .Returns(Task.FromResult("placeholder"));

        var definition = new ToolDefinition
        {
            Type = "mcp",
            Mcp = new McpConfig
            {
                Server = "npx",
                Env = new Dictionary<string, string> { ["TOKEN"] = "{secret:any-path}" }
            }
        };

        await resolver.ResolveAsync(Workspace, definition);

        captured.ShouldNotBeEmpty();
        captured.ShouldAllBe(t => !t.Grants.Contains("secret:*"));
    }
}
