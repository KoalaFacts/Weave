using Microsoft.Extensions.Logging;
using Orleans;
using Weave.Security.Proxy;
using Weave.Security.Tokens;
using Weave.Security.Vault;

namespace Weave.Security.Grains;

public sealed class SecretProxyGrain : Grain, ISecretProxyGrain
{
    private readonly TransparentSecretProxy _proxy;
    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<SecretProxyGrain> _logger;

    public SecretProxyGrain(
        TransparentSecretProxy proxy,
        ISecretProvider secretProvider,
        ILogger<SecretProxyGrain> logger)
    {
        _proxy = proxy;
        _secretProvider = secretProvider;
        _logger = logger;
    }

    public async Task<string> RegisterSecretAsync(string secretPath, CapabilityToken token)
    {
        var secret = await _secretProvider.ResolveAsync(secretPath, token);
        var placeholder = $"{{secret:{secretPath}}}";
        _proxy.RegisterSecret(secretPath, secret);
        _logger.LogInformation("Registered secret proxy for '{Path}' in workspace {Workspace}",
            secretPath, this.GetPrimaryKeyString());
        return placeholder;
    }

    public Task UnregisterSecretAsync(string secretPath)
    {
        _proxy.UnregisterSecret(secretPath);
        return Task.CompletedTask;
    }

    public Task<string> SubstituteAsync(string content)
    {
        var result = _proxy.SubstitutePlaceholders(content);
        return Task.FromResult(result);
    }
}
