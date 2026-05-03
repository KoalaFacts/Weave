using Microsoft.Extensions.Logging;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Agents.Pipeline;

internal sealed class SkillMemoryPromptEnricher(
    IVirtualActorProvider actors,
    ICapabilityTokenService tokenService,
    ILogger logger)
{
    public async Task<SkillMemoryEnrichment> EnrichAsync(AgentState state, string messageContent, string? prompt)
    {
        var manifestCapabilities = state.Definition?.Capabilities;
        if (manifestCapabilities is null || !CapabilityGrants.Matches(manifestCapabilities, "skill:read"))
            return new SkillMemoryEnrichment(prompt, []);

        try
        {
            var skillActor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
            var token = MintToken(state.WorkspaceId, state.AgentName, "skill:read");
            var results = await skillActor.SearchAsync(
                messageContent,
                token,
                3,
                new SkillSearchOptions { MinSuccessRate = 0.5, PreferRecent = true });
            if (results.Count == 0)
                return new SkillMemoryEnrichment(prompt, []);

            var skillSection = string.Join("\n\n", results.Select(r =>
                $"### {r.Skill.Title} (relevance: {r.RelevanceScore:F1})\n{r.Skill.Description}\nSteps: {string.Join(" -> ", r.Skill.Steps.OrderBy(s => s.Order).Select(s => s.Action))}"));

            var block = $"[Relevant skills from memory]\n{skillSection}";
            var enrichedPrompt = string.IsNullOrWhiteSpace(prompt)
                ? block
                : $"{prompt}\n\n{block}";
            var skillIds = results.Select(r => r.Skill.SkillId).Distinct().ToList();

            return new SkillMemoryEnrichment(enrichedPrompt, skillIds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to retrieve skills for agent {AgentName}", state.AgentName);
            return new SkillMemoryEnrichment(prompt, []);
        }
    }

    public async Task RecordSuccessfulUsageAsync(AgentState state, IReadOnlyList<SkillId> skillIds)
    {
        if (skillIds.Count == 0)
            return;

        var manifestCapabilities = state.Definition?.Capabilities;
        if (manifestCapabilities is null || !CapabilityGrants.Matches(manifestCapabilities, "skill:write"))
            return;

        try
        {
            var skillActor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
            var token = MintToken(state.WorkspaceId, state.AgentName, "skill:write");
            foreach (var skillId in skillIds)
                await skillActor.RecordUsageAsync(skillId, success: true, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record skill memory usage for agent {AgentName}", state.AgentName);
        }
    }

    private CapabilityToken MintToken(WorkspaceId workspaceId, string agentName, string grant) =>
        tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId.ToString(),
            IssuedTo = $"{workspaceId}/{agentName}",
            Grants = [grant],
            Lifetime = TimeSpan.FromMinutes(1)
        });
}
