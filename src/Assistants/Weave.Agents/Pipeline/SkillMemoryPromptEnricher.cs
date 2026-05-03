using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Agents.Pipeline;

internal sealed class SkillMemoryPromptEnricher(
    IVirtualActorProvider actors,
    ICapabilityTokenService tokenService)
{
    public async Task<SkillMemoryEnrichment> EnrichAsync(AgentState state, string messageContent, string? prompt)
    {
        if (state.Definition?.Capabilities is not { } capabilities || !CapabilityToken.HasGrant(capabilities, "skill:read"))
            return new SkillMemoryEnrichment(prompt, []);

        var skillActor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
        using var source = MintToken(state.WorkspaceId, state.AgentName, "skill:read");
        var results = await skillActor.SearchAsync(
            messageContent,
            source.Token,
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

    public async Task RecordSuccessfulUsageAsync(AgentState state, IReadOnlyList<SkillId> skillIds)
    {
        if (skillIds.Count == 0)
            return;

        if (state.Definition?.Capabilities is not { } capabilities || !CapabilityToken.HasGrant(capabilities, "skill:write"))
            return;

        var skillActor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
        using var source = MintToken(state.WorkspaceId, state.AgentName, "skill:write");
        foreach (var skillId in skillIds)
            await skillActor.RecordUsageAsync(skillId, success: true, source.Token);
    }

    private CapabilityTokenSource MintToken(WorkspaceId workspaceId, string agentName, string grant) =>
        tokenService.MintLinked(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId.ToString(),
            IssuedTo = $"{workspaceId}/{agentName}",
            Grants = [grant],
            Lifetime = TimeSpan.FromMinutes(1)
        }, CancellationToken.None);
}
