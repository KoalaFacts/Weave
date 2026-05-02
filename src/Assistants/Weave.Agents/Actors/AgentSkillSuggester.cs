using Microsoft.Extensions.Logging;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

internal sealed class AgentSkillSuggester(
    IVirtualActorProvider actors,
    ICapabilityTokenService tokenService,
    ILogger logger)
{
    public async Task SuggestFromTaskAsync(AgentState state, AgentTaskId taskId)
    {
        var task = state.ActiveTasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task?.Proof is null || task.Proof.Items.Count < 2)
            return;

        var skill = AgentSkillExtractor.ExtractFromTask(task, state);
        if (skill is null)
            return;

        try
        {
            var skillActor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
            var token = tokenService.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = state.WorkspaceId.ToString(),
                IssuedTo = $"{state.WorkspaceId}/{state.AgentName}",
                Grants = ["skill:write"],
                Lifetime = TimeSpan.FromMinutes(1)
            });
            await skillActor.SuggestSkillAsync(skill, token, taskId.ToString());
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
