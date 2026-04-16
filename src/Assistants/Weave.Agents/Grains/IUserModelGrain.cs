using Weave.Agents.Models;

namespace Weave.Agents.Grains;

public interface IUserModelGrain : IGrainWithStringKey
{
    Task RecordInteractionAsync(InteractionRecord record);
    Task SetPreferenceAsync(string key, string value);
    Task SetDomainContextAsync(string key, string value);
    Task<UserProfileState> GetProfileAsync();
    Task<string> GetContextSummaryAsync();
    Task ClearAsync();
}
