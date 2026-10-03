using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Weave.Invocations;

namespace Weave.Cli.Commands.Local;

internal static class LocalInvocationStatus
{
    public static string ExecutionState(JsonObject status, string id)
    {
        var invocation = status["invocation"] as JsonObject;
        var result = invocation?["result"] as JsonObject;
        if (Has(invocation, "http_status", 404) && Has(result, "errorCode", "invocation-not-found"))
        {
            var approval = status["approval"] as JsonObject;
            var decision = approval?["result"] as JsonObject;
            if (Has(approval, "http_status", 200) && Has(decision, "invocationId", id)
                && decision["approvalState"] is JsonValue state && state.TryGetValue<string>(out var name)
                && name is "Pending" or "Approved" or "Rejected" or "Expired" or "Cancelled")
                return "NotStarted";
            return "Unconfirmed";
        }
        if (!Has(invocation, "http_status", 200) || !Has(result, "invocationId", id) || !Has(result, "toolName", "files")
            || result["attemptId"] is not JsonValue attemptValue || !attemptValue.TryGetValue<string>(out var attemptId)
            || !Guid.TryParseExact(attemptId, "N", out var attempt) || attempt == Guid.Empty
            || result["outcome"] is not JsonValue outcomeValue || !outcomeValue.TryGetValue<string>(out var outcome))
            return "Unconfirmed";
        if (outcome == nameof(InvocationOutcome.OutcomeUnknown) && Has(result, "success", false)
            && (Has(result, "outcomeRecorded", true) || Has(result, "outcomeRecorded", false)))
            return outcome;
        if (!Has(result, "outcomeRecorded", true))
            return "Unconfirmed";
        return outcome switch
        {
            nameof(InvocationOutcome.Succeeded) when Has(result, "success", true) => outcome,
            nameof(InvocationOutcome.Failed) or nameof(InvocationOutcome.Cancelled) or nameof(InvocationOutcome.Denied)
                or nameof(InvocationOutcome.NotDispatched) when Has(result, "success", false) => outcome,
            _ => "Unconfirmed"
        };
    }

    public static bool CanResume(JsonObject status, string id) => ExecutionState(status, id) == "NotStarted"
        && Has(status["approval"]?["result"] as JsonObject, "approvalState", "Approved");

    private static bool Has<T>([NotNullWhen(true)] JsonObject? node, string field, T expected) => node?[field] is JsonValue value
        && value.TryGetValue<T>(out var actual) && EqualityComparer<T>.Default.Equals(actual, expected);
}
