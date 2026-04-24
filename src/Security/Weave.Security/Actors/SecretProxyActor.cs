using Microsoft.Extensions.Logging;
using Weave.Security.Proxy;
using Weave.Security.Tokens;
using Weave.Security.Vault;

namespace Weave.Security.Actors;

public sealed partial class SecretProxyActor : VirtualActor, ISecretProxyActor
{
    private readonly TransparentSecretProxy _proxy;
    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<SecretProxyActor> _logger;

    public SecretProxyActor(
        TransparentSecretProxy proxy,
        ISecretProvider secretProvider,
        ILogger<SecretProxyActor> logger)
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
        LogSecretRegistered(secretPath, GetWorkspaceKey(token));
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

    private string GetWorkspaceKey(CapabilityToken token)
    {
        try
        {
            return this.GetPrimaryKeyString();
        }
        catch (NullReferenceException)
        {
            return token.WorkspaceId;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered secret proxy for '{Path}' in workspace {Workspace}")]
    private partial void LogSecretRegistered(string path, string workspace);
}
