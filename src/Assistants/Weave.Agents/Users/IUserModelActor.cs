using Weave.Agents.Models;
using Weave.Security.Tokens;

namespace Weave.Agents.Users;

public interface IUserModelActor
{
    Task RecordInteractionAsync(InteractionRecord record, CapabilityToken token);
    Task SetPreferenceAsync(string key, string value, CapabilityToken token);
    Task SetDomainContextAsync(string key, string value, CapabilityToken token);
    Task<UserProfileState> GetProfileAsync(CapabilityToken token);
    Task<string> GetContextSummaryAsync(CapabilityToken token);
    Task ClearAsync(CapabilityToken token);
}
