using Microsoft.Extensions.Logging;
using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

internal sealed class AgentSkillSuggester(
    IVirtualActorProvider actors,
    AgentSkillExtractor extractor,
    ILogger logger)
{
    public async Task SuggestFromTaskAsync(AgentState state, AgentTaskId taskId)
    {
        var task = state.ActiveTasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task?.Proof is null || task.Proof.Items.Count < 2)
            return;

        var skill = extractor.ExtractFromTask(task, state);
        if (skill is null)
            return;

        try
        {
            var skillActor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
            await skillActor.SuggestSkillAsync(skill, taskId.ToString());
            logger.LogInformation(
                "Suggested skill '{Title}' from task {TaskId}",
                skill.Title,
                taskId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to suggest skill from task {TaskId}", taskId);
        }
    }
}
