namespace Weave.Silo.Api;

internal sealed class AgentTaskRequestValidator
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from AgentTaskEndpoints.")]
    public Dictionary<string, string[]>? ValidateSubmitTask(SubmitTaskRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Description))
            (errors ??= [])["description"] = ["Description is required."];
        else if (request.Description.Length > 1000)
            (errors ??= [])["description"] = ["Description must be 1000 characters or fewer."];

        return errors;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from AgentTaskEndpoints.")]
    public Dictionary<string, string[]>? ValidateCompleteTask(CompleteTaskRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (request.Proof is not { Count: > 0 })
            (errors ??= [])["proof"] = ["At least one proof item is required."];
        else
            AddProofItemErrors(request, ref errors);

        return errors;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from AgentTaskEndpoints.")]
    public Dictionary<string, string[]>? ValidateReviewTask(ReviewTaskRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (request.Feedback is { Length: > 5000 })
            (errors ??= [])["feedback"] = ["Feedback must be 5000 characters or fewer."];

        return errors;
    }

    private static void AddProofItemErrors(CompleteTaskRequest request, ref Dictionary<string, string[]>? errors)
    {
        for (var index = 0; index < request.Proof.Count; index++)
        {
            var item = request.Proof[index];
            if (string.IsNullOrWhiteSpace(item.Label))
                (errors ??= [])[$"proof[{index}].label"] = ["Label is required."];
            if (string.IsNullOrWhiteSpace(item.Value))
                (errors ??= [])[$"proof[{index}].value"] = ["Value is required."];
        }
    }
}
