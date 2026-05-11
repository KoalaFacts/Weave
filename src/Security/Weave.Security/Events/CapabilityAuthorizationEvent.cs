using Weave.Shared.Events;

namespace Weave.Security.Events;

/// <summary>
/// Audit row emitted on every <see cref="Tokens.ICapabilityAuthorizer.AuthorizeAsync"/>
/// call — allow or deny. Keys: <see cref="TokenId"/>, <see cref="Grant"/>,
/// <see cref="WorkspaceId"/>, <see cref="IssuedTo"/>, <see cref="Outcome"/>,
/// <see cref="ActionContext"/>, and <see cref="Reason"/> on denies.
/// </summary>
public sealed record CapabilityAuthorizationEvent : DomainEvent
{
    public required string TokenId { get; init; }
    public required string Grant { get; init; }
    public required string IssuedTo { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ActionContext { get; init; }
    public required CapabilityAuthorizationOutcome Outcome { get; init; }

    /// <summary>
    /// Set on <see cref="CapabilityAuthorizationOutcome.Deny"/>. One of
    /// <c>"invalid-or-expired-token"</c>, <c>"workspace-mismatch"</c>,
    /// <c>"grant-missing"</c>. Null on allow.
    /// </summary>
    public string? Reason { get; init; }
}

public enum CapabilityAuthorizationOutcome
{
    Allow,
    Deny
}
