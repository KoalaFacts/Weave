using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Events;

namespace Weave.Silo.VirtualActors;

public sealed class UserModelActorGrain : Grain, IUserModelActorGrain
{
    private readonly UserModelActor _actor;

    public UserModelActorGrain(
        IEventBus eventBus,
        TimeProvider timeProvider,
        ICapabilityAuthorizer authorizer,
        ILogger<UserModelActor> logger,
        [PersistentState("user-model", "Default")] IPersistentState<UserProfileState> state)
    {
        _actor = new UserModelActor(eventBus, timeProvider, authorizer, logger,
            new OrleansActorState<UserProfileState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task RecordInteractionAsync(InteractionRecord record, CapabilityToken token) =>
        _actor.RecordInteractionAsync(record, token);
    public Task SetPreferenceAsync(string key, string value, CapabilityToken token) =>
        _actor.SetPreferenceAsync(key, value, token);
    public Task SetDomainContextAsync(string key, string value, CapabilityToken token) =>
        _actor.SetDomainContextAsync(key, value, token);
    public Task<UserProfileState> GetProfileAsync(CapabilityToken token) => _actor.GetProfileAsync(token);
    public Task<string> GetContextSummaryAsync(CapabilityToken token) => _actor.GetContextSummaryAsync(token);
    public Task ClearAsync(CapabilityToken token) => _actor.ClearAsync(token);
}
