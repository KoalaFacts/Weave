using Microsoft.Extensions.Logging;

namespace Weave.Security.Tokens;

/// <summary>
/// Single capability-check seam: validate signature, enforce token-workspace
/// match, enforce grant, log every denial. Per principle 2 of
/// docs/unique-agent-strategy.md (fail closed, log loud).
/// </summary>
public static class CapabilityAuthorizer
{
    public static void Authorize(
        ICapabilityTokenService tokenService,
        CapabilityToken token,
        string actorWorkspaceId,
        string grant,
        ILogger logger,
        string domain)
    {
        if (!tokenService.Validate(token))
        {
            logger.LogWarning(
                "{Domain} capability denied: invalid or expired token for grant '{Grant}' on workspace {WorkspaceId}",
                domain, grant, actorWorkspaceId);
            throw new UnauthorizedAccessException("Invalid or expired capability token");
        }

        if (!string.IsNullOrWhiteSpace(actorWorkspaceId)
            && !string.Equals(token.WorkspaceId, actorWorkspaceId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "{Domain} capability denied: token workspace '{TokenWorkspaceId}' does not match actor workspace '{ActorWorkspaceId}' for grant '{Grant}'",
                domain, token.WorkspaceId, actorWorkspaceId, grant);
            throw new UnauthorizedAccessException(
                $"Token workspace '{token.WorkspaceId}' does not match actor workspace '{actorWorkspaceId}'");
        }

        if (!token.HasGrant(grant))
        {
            logger.LogWarning(
                "{Domain} capability denied: token issued to '{IssuedTo}' does not grant '{Grant}' on workspace {WorkspaceId}",
                domain, token.IssuedTo, grant, actorWorkspaceId);
            throw new UnauthorizedAccessException($"Token does not grant '{grant}'");
        }
    }
}
