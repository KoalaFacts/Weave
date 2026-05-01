using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Silo.Api;

internal sealed class SkillDocumentMapper
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from SkillEndpoints.")]
    public SkillDocument FromRequest(StoreSkillRequest request)
    {
        return new SkillDocument
        {
            SkillId = SkillId.New(),
            Title = request.Title,
            Description = request.Description,
            Tags = request.Tags,
            Steps = request.Steps.Select((step, index) => new SkillStep
            {
                Order = index,
                Action = step.Action,
                ToolName = step.ToolName,
                ExpectedOutcome = step.ExpectedOutcome
            }).ToList(),
            ToolsUsed = request.ToolsUsed,
            CreatedByAgent = request.CreatedByAgent,
            OriginTaskDescription = request.OriginTaskDescription
        };
    }
}
