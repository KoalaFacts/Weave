namespace Weave.Silo.Api;

internal sealed class SkillRequestValidator
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from SkillEndpoints.")]
    public Dictionary<string, string[]>? ValidateStoreSkill(StoreSkillRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Title))
            (errors ??= [])["title"] = ["Title is required."];
        if (string.IsNullOrWhiteSpace(request.Description))
            (errors ??= [])["description"] = ["Description is required."];
        if (request.Steps is not { Count: > 0 })
            (errors ??= [])["steps"] = ["At least one step is required."];
        if (string.IsNullOrWhiteSpace(request.CreatedByAgent))
            (errors ??= [])["createdByAgent"] = ["CreatedByAgent is required."];

        return errors;
    }
}
