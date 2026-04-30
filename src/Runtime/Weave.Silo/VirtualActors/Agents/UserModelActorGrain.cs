using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Events;

namespace Weave.Silo.VirtualActors;

public sealed class UserModelActorGrain : Grain, IUserModelActorGrain
{
    private readonly UserModelActor _actor;

    public UserModelActorGrain(
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<UserModelActor> logger,
        [PersistentState("user-model", "Default")] IPersistentState<UserProfileState> state)
    {
        _actor = new UserModelActor(eventBus, timeProvider, logger,
            new OrleansActorState<UserProfileState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task RecordInteractionAsync(InteractionRecord record) => _actor.RecordInteractionAsync(record);
    public Task SetPreferenceAsync(string key, string value) => _actor.SetPreferenceAsync(key, value);
    public Task SetDomainContextAsync(string key, string value) => _actor.SetDomainContextAsync(key, value);
    public Task<UserProfileState> GetProfileAsync() => _actor.GetProfileAsync();
    public Task<string> GetContextSummaryAsync() => _actor.GetContextSummaryAsync();
    public Task ClearAsync() => _actor.ClearAsync();
}
