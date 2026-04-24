using Microsoft.Extensions.Logging;
using Orleans;
using Weave.Security.Actors;
using Weave.Security.Proxy;
using Weave.Security.Tokens;
using Weave.Security.Vault;

namespace Weave.Silo.VirtualActors;

public sealed class SecretProxyActorGrain : Grain, ISecretProxyActorGrain
{
    private readonly SecretProxyActor _actor;

    public SecretProxyActorGrain(
        TransparentSecretProxy proxy,
        ISecretProvider secretProvider,
        ILogger<SecretProxyActor> logger)
    {
        _actor = new SecretProxyActor(proxy, secretProvider, logger);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<string> RegisterSecretAsync(string secretPath, CapabilityToken token) =>
        _actor.RegisterSecretAsync(secretPath, token);

    public Task UnregisterSecretAsync(string secretPath) => _actor.UnregisterSecretAsync(secretPath);
    public Task<string> SubstituteAsync(string content) => _actor.SubstituteAsync(content);
}
