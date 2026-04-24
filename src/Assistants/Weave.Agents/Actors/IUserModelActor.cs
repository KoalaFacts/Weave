using Weave.Agents.Models;

namespace Weave.Agents.Actors;

public interface IUserModelActor : IVirtualActorWithStringKey
{
    Task RecordInteractionAsync(InteractionRecord record);
    Task SetPreferenceAsync(string key, string value);
    Task SetDomainContextAsync(string key, string value);
    Task<UserProfileState> GetProfileAsync();
    Task<string> GetContextSummaryAsync();
    Task ClearAsync();
}
