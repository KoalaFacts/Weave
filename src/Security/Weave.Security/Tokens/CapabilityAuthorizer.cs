using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Weave.Security.Events;
using Weave.Shared.Events;

namespace Weave.Security.Tokens;

/// <summary>
/// Single enforcement point for capability checks. Replaces the per-actor
/// private <c>Authorize</c> copies that previously lived in <c>ToolActor</c>,
/// <c>SkillMemoryActor</c>, <c>ChannelGatewayActor</c>, <c>UserModelActor</c>,
/// and <c>PluginRegistry</c>. Validates token signature/expiry/revocation,
/// optional workspace match, and the requested grant. Emits a
/// <see cref="CapabilityAuthorizationEvent"/> on every call (allow or deny)
/// so audit log + future replay/debugger can read one stream.
/// </summary>
public interface ICapabilityAuthorizer
{
    /// <param name="token">The capability token presented by the caller.</param>
    /// <param name="grant">The grant string the action requires (e.g. <c>"tool:git"</c>).</param>
    /// <param name="actorWorkspaceId">
    /// The workspace the actor is bound to. When non-empty, the token's
    /// <see cref="CapabilityToken.WorkspaceId"/> must match ordinally; mismatch denies.
    /// When null or empty, the workspace check is skipped — used by silo-wide
    /// services such as <c>PluginRegistry</c>.
    /// </param>
    /// <param name="actionContext">
    /// Caller-provided context string describing the call site
    /// (e.g. <c>"ToolActor.InvokeAsync"</c>). Defaults to the calling member name.
    /// Recorded on every audit row so the replay path can group by action.
    /// Truncated to <see cref="CapabilityAuthorizer.MaxActionContextLength"/>
    /// characters and stripped of control chars before being logged or stored —
    /// callers should not pass user-controlled strings here without their own
    /// validation.
    /// </param>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown on invalid/expired token, workspace mismatch, or missing grant.
    /// An audit deny event is published before the throw.
    /// </exception>
    Task AuthorizeAsync(
        CapabilityToken token,
        string grant,
        string? actorWorkspaceId,
        [CallerMemberName] string actionContext = "");
}

public sealed partial class CapabilityAuthorizer(
    ICapabilityTokenService tokenService,
    IEventBus eventBus,
    ILogger<CapabilityAuthorizer> logger) : ICapabilityAuthorizer
{
    private const string ReasonInvalidToken = "invalid-or-expired-token";
    private const string ReasonWorkspaceMismatch = "workspace-mismatch";
    private const string ReasonGrantMissing = "grant-missing";

    /// <summary>
    /// Maximum characters of <c>actionContext</c> recorded on the audit row
    /// or written to the structured log. Today every call site passes a
    /// short literal, but the cap defends against a future caller plumbing
    /// user input through.
    /// </summary>
    public const int MaxActionContextLength = 200;

    public async Task AuthorizeAsync(
        CapabilityToken token,
        string grant,
        string? actorWorkspaceId,
        [CallerMemberName] string actionContext = "")
    {
        var safeActionContext = SanitizeActionContext(actionContext);

        if (!tokenService.Validate(token))
        {
            LogDenyInvalidToken(grant, actorWorkspaceId ?? string.Empty, safeActionContext);
            await PublishAsync(token, grant, safeActionContext, CapabilityAuthorizationOutcome.Deny, ReasonInvalidToken);
            throw new UnauthorizedAccessException("Invalid or expired capability token");
        }

        if (!string.IsNullOrWhiteSpace(actorWorkspaceId)
            && !string.Equals(token.WorkspaceId, actorWorkspaceId, StringComparison.Ordinal))
        {
            LogDenyWorkspaceMismatch(token.WorkspaceId, actorWorkspaceId, grant, safeActionContext);
            await PublishAsync(token, grant, safeActionContext, CapabilityAuthorizationOutcome.Deny, ReasonWorkspaceMismatch);
            throw new UnauthorizedAccessException(
                $"Token workspace '{token.WorkspaceId}' does not match actor workspace '{actorWorkspaceId}'");
        }

        if (!token.HasGrant(grant))
        {
            LogDenyGrantMissing(token.IssuedTo, grant, safeActionContext);
            await PublishAsync(token, grant, safeActionContext, CapabilityAuthorizationOutcome.Deny, ReasonGrantMissing);
            throw new UnauthorizedAccessException($"Token does not grant '{grant}'");
        }

        await PublishAsync(token, grant, safeActionContext, CapabilityAuthorizationOutcome.Allow, reason: null);
    }

    private Task PublishAsync(
        CapabilityToken token,
        string grant,
        string actionContext,
        CapabilityAuthorizationOutcome outcome,
        string? reason)
    {
        // CapabilityTokenService.Mint rejects empty WorkspaceId, so this is
        // always "{ws}/{tokenId}" in practice; no fallback branch is needed.
        var sourceId = $"{token.WorkspaceId}/{token.TokenId}";

        return eventBus.PublishAsync(new CapabilityAuthorizationEvent
        {
            SourceId = sourceId,
            TokenId = token.TokenId,
            Grant = grant,
            IssuedTo = token.IssuedTo,
            WorkspaceId = token.WorkspaceId,
            ActionContext = actionContext,
            Outcome = outcome,
            Reason = reason
        }, token.CancellationToken);
    }

    private static string SanitizeActionContext(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var trimmed = input.Length > MaxActionContextLength
            ? input[..MaxActionContextLength]
            : input;

        var hasControl = false;
        foreach (var c in trimmed)
        {
            if (char.IsControl(c))
            {
                hasControl = true;
                break;
            }
        }

        if (!hasControl)
            return trimmed;

        return string.Create(trimmed.Length, trimmed, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                span[i] = char.IsControl(src[i]) ? '?' : src[i];
        });
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Capability denied: invalid or expired token for grant '{Grant}' on workspace '{ActorWorkspaceId}' ({ActionContext})")]
    private partial void LogDenyInvalidToken(string grant, string actorWorkspaceId, string actionContext);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Capability denied: token workspace '{TokenWorkspaceId}' does not match actor workspace '{ActorWorkspaceId}' for grant '{Grant}' ({ActionContext})")]
    private partial void LogDenyWorkspaceMismatch(string tokenWorkspaceId, string actorWorkspaceId, string grant, string actionContext);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Capability denied: token issued to '{IssuedTo}' does not grant '{Grant}' ({ActionContext})")]
    private partial void LogDenyGrantMissing(string issuedTo, string grant, string actionContext);
}
