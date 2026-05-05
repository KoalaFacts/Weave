using System.Diagnostics.CodeAnalysis;

namespace Weave.Actions.Context;

/// <summary>
/// Typed result of an action: either a successful <typeparamref name="TValue"/>
/// or a structured <see cref="ActionFailure"/>. Frontends switch on
/// <see cref="IsSuccess"/> to render success vs. failure paths.
/// Construct via <see cref="ActionResult.Success{T}"/> or
/// <see cref="ActionResult.Failed{T}"/>.
/// </summary>
public readonly record struct ActionResult<TValue>
{
    internal ActionResult(bool isSuccess, TValue? value, ActionFailure? failure)
    {
        IsSuccess = isSuccess;
        Value = value;
        Failure = failure;
    }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess { get; }

    public TValue? Value { get; }

    public ActionFailure? Failure { get; }
}

/// <summary>
/// Non-generic factory companion for <see cref="ActionResult{TValue}"/>.
/// Lets call sites use type-inferred construction
/// (<c>ActionResult.Success(value)</c>) instead of restating the type.
/// </summary>
public static class ActionResult
{
    public static ActionResult<TValue> Success<TValue>(TValue value) =>
        new(isSuccess: true, value, failure: null);

    public static ActionResult<TValue> Failed<TValue>(ActionFailure failure) =>
        new(isSuccess: false, default, failure);
}
