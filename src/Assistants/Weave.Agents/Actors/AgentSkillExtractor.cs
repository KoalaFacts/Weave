using Weave.Agents.Models;

namespace Weave.Agents.Actors;

internal sealed class AgentSkillExtractor
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from AgentActor.")]
    public SkillDocument? ExtractFromTask(AgentTaskInfo task, AgentState state)
    {
        if (task.Proof is null || task.Proof.Items.Count < 2)
            return null;

        var toolsUsed = task.Proof.Items
            .Where(p => p.Type is ProofType.Custom or ProofType.DiffSummary)
            .Select(p => p.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var steps = task.Proof.Items.Select((item, index) => new SkillStep
        {
            Order = index,
            Action = $"{item.Type}: {item.Label}",
            ToolName = item.Type == ProofType.Custom ? item.Label : null,
            ExpectedOutcome = item.Value.Length > 200 ? item.Value[..200] : item.Value
        }).ToList();

        return new SkillDocument
        {
            SkillId = Shared.Ids.SkillId.New(),
            Title = task.Description.Length > 100 ? task.Description[..100] : task.Description,
            Description = $"Auto-extracted from completed task: {task.Description}",
            Tags = toolsUsed,
            Steps = steps,
            ToolsUsed = state.ConnectedTools.ToList(),
            CreatedByAgent = state.AgentName,
            OriginTaskDescription = task.Description
        };
    }
}
