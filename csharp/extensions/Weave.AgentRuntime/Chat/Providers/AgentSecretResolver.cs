using Microsoft.Extensions.Logging;

namespace Weave.Agents.Pipeline.Providers;

/// <summary>Default <see cref="IAgentSecretResolver"/>; resolves the <c>env/</c> backend.</summary>
public sealed class AgentSecretResolver(ILogger<AgentSecretResolver> logger) : IAgentSecretResolver
{
    private const string Prefix = "{secret:";
    private const char Suffix = '}';

    public Task<string?> ResolveAsync(string? placeholder, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(placeholder))
            return Task.FromResult<string?>(null);

        if (!placeholder.StartsWith(Prefix, StringComparison.Ordinal) || placeholder[^1] != Suffix)
        {
            logger.LogWarning(
                "Agent secret reference must be a {{secret:backend/path}} placeholder; literal values are ignored to avoid hardcoded keys in manifests.");
            return Task.FromResult<string?>(null);
        }

        var path = placeholder[Prefix.Length..^1];
        var slashIdx = path.IndexOf('/');
        if (slashIdx <= 0 || slashIdx == path.Length - 1)
        {
            logger.LogWarning("Agent secret placeholder {Placeholder} is missing a backend or path.", placeholder);
            return Task.FromResult<string?>(null);
        }

        var backend = path[..slashIdx];
        var key = path[(slashIdx + 1)..];

        return backend switch
        {
            "env" => Task.FromResult(Environment.GetEnvironmentVariable(key)),
            "vault" => DeferVault(placeholder),
            _ => Unknown(backend, placeholder)
        };
    }

    private Task<string?> DeferVault(string placeholder)
    {
        logger.LogWarning(
            "{{secret:vault/...}} resolution is deferred to a follow-up (CapabilityToken plumbing); placeholder {Placeholder} resolved to null.",
            placeholder);
        return Task.FromResult<string?>(null);
    }

    private Task<string?> Unknown(string backend, string placeholder)
    {
        logger.LogWarning("Unknown secret backend {Backend} in placeholder {Placeholder}.", backend, placeholder);
        return Task.FromResult<string?>(null);
    }
}
