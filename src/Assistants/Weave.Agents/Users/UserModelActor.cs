using Microsoft.Extensions.Logging;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Users;

public sealed class UserModelActor(
    IEventBus eventBus,
    TimeProvider timeProvider,
    ICapabilityAuthorizer authorizer,
    ILogger<UserModelActor> logger,
    IActorState<UserProfileState> persistentState) : IUserModelActor
{
    private string? _key;

    public async Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _key = key;
        await persistentState.ReadStateAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(persistentState.State.UserId))
        {
            ApplyIdentity(key);
            await persistentState.WriteStateAsync(cancellationToken);
        }
    }

    public async Task RecordInteractionAsync(InteractionRecord record, CapabilityToken token)
    {
        EnsureIdentity();
        await authorizer.AuthorizeAsync(token, BuildGrant(write: true), persistentState.State.WorkspaceId);

        persistentState.State.RecordInteraction(record, timeProvider.GetUtcNow());
        await persistentState.WriteStateAsync(token.CancellationToken);

        await eventBus.PublishAsync(new UserInteractionRecordedEvent
        {
            SourceId = $"{persistentState.State.WorkspaceId}/{persistentState.State.UserId}",
            WorkspaceId = WorkspaceId.From(persistentState.State.WorkspaceId),
            UserId = persistentState.State.UserId,
            AgentName = record.AgentName
        }, token.CancellationToken);

        logger.LogInformation(
            "Recorded interaction for user {UserId} with agent {AgentName}",
            persistentState.State.UserId,
            record.AgentName);
    }

    public async Task SetPreferenceAsync(string key, string value, CapabilityToken token)
    {
        EnsureIdentity();
        await authorizer.AuthorizeAsync(token, BuildGrant(write: true), persistentState.State.WorkspaceId);
        persistentState.State.Preferences[key] = value;
        await persistentState.WriteStateAsync(token.CancellationToken);
    }

    public async Task SetDomainContextAsync(string key, string value, CapabilityToken token)
    {
        EnsureIdentity();
        await authorizer.AuthorizeAsync(token, BuildGrant(write: true), persistentState.State.WorkspaceId);
        persistentState.State.DomainContext[key] = value;
        await persistentState.WriteStateAsync(token.CancellationToken);
    }

    public async Task<UserProfileState> GetProfileAsync(CapabilityToken token)
    {
        EnsureIdentity();
        await authorizer.AuthorizeAsync(token, BuildGrant(write: false), persistentState.State.WorkspaceId);
        return persistentState.State;
    }

    public async Task<string> GetContextSummaryAsync(CapabilityToken token)
    {
        EnsureIdentity();
        await authorizer.AuthorizeAsync(token, BuildGrant(write: false), persistentState.State.WorkspaceId);
        return persistentState.State.BuildContextSummary();
    }

    public async Task ClearAsync(CapabilityToken token)
    {
        EnsureIdentity();
        await authorizer.AuthorizeAsync(token, BuildGrant(write: true), persistentState.State.WorkspaceId);
        persistentState.State.Clear();
        await persistentState.WriteStateAsync(token.CancellationToken);
    }

    private void EnsureIdentity()
    {
        if (!string.IsNullOrWhiteSpace(persistentState.State.UserId))
            return;

        ApplyIdentity(_key);
    }

    private void ApplyIdentity(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var parts = key.Split('/', 2);
        persistentState.State.WorkspaceId = parts.Length > 1 ? parts[0] : key;
        persistentState.State.UserId = parts.Length > 1 ? parts[1] : key;
    }

    private string BuildGrant(bool write) =>
        $"user:{(write ? "write" : "read")}:{persistentState.State.UserId}";
}
