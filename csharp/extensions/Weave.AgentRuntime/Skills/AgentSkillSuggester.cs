using Microsoft.Extensions.Logging;
using Weave.Agents.Lifecycle;
using Weave.Security.Tokens;
using Weave.Shared.Capabilities;
using Weave.Shared.Ids;

namespace Weave.Agents.Skills;

internal sealed class AgentSkillSuggester(
    IVirtualActorProvider actors,
    ICapabilityTokenService tokenService,
    ILogger logger)
{
    public async Task SuggestFromTaskAsync(AgentState state, AgentTaskId taskId)
    {
        var task = state.ActiveTasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null)
            return;

        var skill = AgentSkillExtractor.ExtractFromTask(task, state);
        if (skill is null)
            return;

        if (state.Definition?.Capabilities is not { } capabilities || !CapabilityGrantMatcher.HasGrant(capabilities, "skill:write"))
        {
            logger.LogInformation(
                "Skipping skill suggestion from task {TaskId}: agent '{AgentName}' manifest does not declare 'skill:write'",
                taskId,
                state.AgentName);
            return;
        }

        var skillActor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
        using var source = tokenService.MintLinked(new CapabilityTokenRequest
        {
            WorkspaceId = state.WorkspaceId.ToString(),
            IssuedTo = $"{state.WorkspaceId}/{state.AgentName}",
            Grants = ["skill:write"],
            Lifetime = TimeSpan.FromMinutes(1)
        }, CancellationToken.None);
        await skillActor.SuggestSkillAsync(skill, source.Token, taskId.ToString());
        logger.LogInformation(
            "Suggested skill '{Title}' from task {TaskId}",
            skill.Title,
            taskId);
    }
}
