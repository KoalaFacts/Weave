using Weave.Security.Actors;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Manifest;
namespace Weave.Agents.ToolRegistry;

internal sealed class ToolSecretResolver(
    IVirtualActorProvider actors,
    ICapabilityTokenService tokenService)
{
    public async Task<ToolDefinition> ResolveAsync(string workspaceId, ToolDefinition definition)
    {
        var paths = EnumerateAllSecretPaths(definition).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0)
            return definition;

        var proxy = actors.GetActor<ISecretProxyActor>(VirtualActorId.From(workspaceId));
        using var source = tokenService.MintLinked(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = $"{workspaceId}/tool-registry",
            Grants = [.. paths.Select(p => $"secret:{p}")],
            Lifetime = TimeSpan.FromHours(1)
        }, CancellationToken.None);

        var mcpEnv = definition.Mcp is null
            ? null
            : await ResolveAsync(definition.Mcp.Env, proxy, source.Token);
        var authToken = definition.OpenApi?.Auth?.Token;
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            foreach (var secretPath in SecretPlaceholderParser.EnumeratePaths(authToken))
            {
                await proxy.RegisterSecretAsync(secretPath, source.Token);
            }

            authToken = await proxy.SubstituteAsync(authToken);
        }

        return definition with
        {
            Mcp = definition.Mcp is null ? null : definition.Mcp with { Env = mcpEnv ?? [] },
            OpenApi = definition.OpenApi is null
                ? null
                : definition.OpenApi with
                {
                    Auth = definition.OpenApi.Auth is null
                        ? null
                        : definition.OpenApi.Auth with { Token = authToken }
                }
        };
    }

    private static IEnumerable<string> EnumerateAllSecretPaths(ToolDefinition definition)
    {
        if (definition.Mcp is not null)
        {
            foreach (var (_, value) in definition.Mcp.Env)
                foreach (var path in SecretPlaceholderParser.EnumeratePaths(value))
                    yield return path;
        }

        if (definition.OpenApi?.Auth?.Token is { Length: > 0 } authToken)
        {
            foreach (var path in SecretPlaceholderParser.EnumeratePaths(authToken))
                yield return path;
        }
    }

    private static async Task<Dictionary<string, string>> ResolveAsync(
        Dictionary<string, string> source,
        ISecretProxyActor proxy,
        CapabilityToken token)
    {
        var result = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            foreach (var secretPath in SecretPlaceholderParser.EnumeratePaths(value))
            {
                await proxy.RegisterSecretAsync(secretPath, token);
            }

            result[key] = await proxy.SubstituteAsync(value);
        }

        return result;
    }
}
