namespace Weave.Actions.Context;

/// <summary>
/// Structured failure carried by <see cref="ActionResult{T}"/>. Frontends switch
/// on <see cref="Reason"/> to choose the rendering shape; <see cref="Message"/>
/// is human-friendly text already-rendered for the user.
/// </summary>
public sealed record ActionFailure(ActionFailureReason Reason, string Message)
{
    public static ActionFailure SiloUnreachable(string message) =>
        new(ActionFailureReason.SiloUnreachable, message);

    public static ActionFailure ValidationFailed(string message) =>
        new(ActionFailureReason.ValidationFailed, message);

    public static ActionFailure NotFound(string message) =>
        new(ActionFailureReason.NotFound, message);

    public static ActionFailure Conflict(string message) =>
        new(ActionFailureReason.Conflict, message);

    public static ActionFailure Unauthorized(string message) =>
        new(ActionFailureReason.Unauthorized, message);

    public static ActionFailure Cancelled(string message = "Operation cancelled.") =>
        new(ActionFailureReason.Cancelled, message);

    public static ActionFailure Internal(string message) =>
        new(ActionFailureReason.Internal, message);
}
