namespace Weave.Actions.Context;

/// <summary>
/// Stable categorical reason for an <see cref="ActionFailure"/>. Frontends switch
/// on this to decide the rendering shape (e.g., "silo-unreachable" → tip about
/// <c>weave workspace up</c>; "validation-failed" → highlight the offending
/// fields).
/// </summary>
public enum ActionFailureReason
{
    Unknown = 0,
    SiloUnreachable,
    ValidationFailed,
    NotFound,
    Conflict,
    Unauthorized,
    Cancelled,
    Internal
}
