using Microsoft.Extensions.Logging;
using Weave.Shared.Capabilities;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Actors;

public sealed partial class CapabilityTemplateActor(
    TimeProvider timeProvider,
    ILogger<CapabilityTemplateActor> logger,
    IActorState<TemplateRegistryState> persistentState)
    : ICapabilityTemplateActor
{
    public Task OnActivatedAsync(string? key, CancellationToken cancellationToken) =>
        persistentState.ReadStateAsync(cancellationToken);

    public async Task<CapabilityTemplate> RegisterAsync(CapabilityTemplate template)
    {
        var key = template.TemplateId.ToString();
        template.Status = TemplateStatus.Draft;
        persistentState.State.Templates[key] = template;
        await persistentState.WriteStateAsync();
        LogTemplateRegistered(template.TemplateId, template.Name);
        return template;
    }

    public async Task<CapabilityTemplate> ValidateAndPublishAsync(TemplateId templateId)
    {
        var key = templateId.ToString();
        if (!persistentState.State.Templates.TryGetValue(key, out var template))
            throw new KeyNotFoundException($"Template '{templateId}' not found.");

        var results = Validate(template);
        template.ValidationResults.Clear();
        template.ValidationResults.AddRange(results);

        if (results.TrueForAll(r => r.Passed))
        {
            template.Status = TemplateStatus.Published;
            template.PublishedAt = timeProvider.GetUtcNow();
            LogTemplatePublished(templateId, template.Name);
        }

        persistentState.State.Templates[key] = template;
        await persistentState.WriteStateAsync();
        return template;
    }

    public Task<CapabilityTemplate?> GetAsync(TemplateId templateId)
    {
        var key = templateId.ToString();
        persistentState.State.Templates.TryGetValue(key, out var template);
        return Task.FromResult(template);
    }

    public Task<IReadOnlyList<CapabilityTemplate>> ListPublishedAsync(int offset = 0, int limit = 50)
    {
        // Assign to List<T> (not IReadOnlyList<T>) so the compiler emits
        // a real List, not a synthesized <>z__ReadOnlyList wrapper that
        // Orleans has no codec for. Same pattern applies to every actor
        // method that returns a collection.
        List<CapabilityTemplate> published = [.. persistentState.State.Templates.Values
            .Where(t => t.Status == TemplateStatus.Published)
            .OrderByDescending(t => t.PublishedAt)
            .Skip(offset)
            .Take(limit)];
        return Task.FromResult<IReadOnlyList<CapabilityTemplate>>(published);
    }

    public Task<IReadOnlyList<CapabilityTemplate>> SearchAsync(string? query, int maxResults = 20)
    {
        var candidates = persistentState.State.Templates.Values
            .Where(t => t.Status == TemplateStatus.Published);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            candidates = candidates.Where(t => keywords.Any(k =>
                t.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                t.Tags.Any(tag => tag.Contains(k, StringComparison.OrdinalIgnoreCase))));
        }

        List<CapabilityTemplate> results = [.. candidates.Take(maxResults)];
        return Task.FromResult<IReadOnlyList<CapabilityTemplate>>(results);
    }

    public async Task DeprecateAsync(TemplateId templateId)
    {
        var key = templateId.ToString();
        if (!persistentState.State.Templates.TryGetValue(key, out var template))
            throw new KeyNotFoundException($"Template '{templateId}' not found.");

        template.Status = TemplateStatus.Deprecated;
        persistentState.State.Templates[key] = template;
        await persistentState.WriteStateAsync();
    }

    public async Task IncrementInstantiationCountAsync(TemplateId templateId)
    {
        var key = templateId.ToString();
        if (!persistentState.State.Templates.TryGetValue(key, out var template))
            throw new KeyNotFoundException($"Template '{templateId}' not found.");

        template.InstantiationCount++;
        persistentState.State.Templates[key] = template;
        await persistentState.WriteStateAsync();
    }

    internal static List<TemplateValidationResult> Validate(CapabilityTemplate template)
    {
        var results = new List<TemplateValidationResult>();

        var hasModel = !string.IsNullOrWhiteSpace(template.AgentDefinition.Model);
        results.Add(new TemplateValidationResult
        {
            Check = "AgentModelPresent",
            Passed = hasModel,
            Detail = hasModel ? null : "Agent definition must specify a Model."
        });

        // STJ/Orleans round-trips can leave init-only collection properties as
        // null when the wire payload omits the field, so coalesce defensively.
        // Null means "no grants declared" — the same as an empty list — and
        // we want validation to fail closed, not throw.
        var ownedGrants = template.AgentDefinition.Capabilities ?? [];

        foreach (var toolRef in template.AgentDefinition.Tools)
        {
            var found = template.RequiredTools.ContainsKey(toolRef);
            results.Add(new TemplateValidationResult
            {
                Check = $"ToolPresent:{toolRef}",
                Passed = found,
                Detail = found ? null : $"Tool '{toolRef}' is referenced by the agent but not present in RequiredTools."
            });

            // A template that hands an agent a tool but no grant covering it is
            // incoherent: the runtime will deny every invocation. Validation
            // refuses to publish such a template — this is the contract the
            // strategy doc names ("pre-validated capability bundles").
            var grant = $"tool:{toolRef}";
            var granted = CapabilityGrantMatcher.HasGrant(ownedGrants, grant);
            results.Add(new TemplateValidationResult
            {
                Check = $"ToolCapabilityGranted:{toolRef}",
                Passed = granted,
                Detail = granted ? null : $"Agent references tool '{toolRef}' but no capability grant covers '{grant}'. Add it (or a wildcard like 'tool:*') to AgentDefinition.Capabilities."
            });
        }

        foreach (var required in template.RequiredCapabilities)
        {
            var granted = CapabilityGrantMatcher.HasGrant(ownedGrants, required);
            results.Add(new TemplateValidationResult
            {
                Check = $"RequiredCapabilityGranted:{required}",
                Passed = granted,
                Detail = granted ? null : $"Template declares required capability '{required}' but no grant in AgentDefinition.Capabilities covers it."
            });
        }

        foreach (var (toolName, toolDef) in template.RequiredTools)
        {
            var hasType = !string.IsNullOrWhiteSpace(toolDef.Type);
            results.Add(new TemplateValidationResult
            {
                Check = $"ToolTypeValid:{toolName}",
                Passed = hasType,
                Detail = hasType ? null : $"Tool '{toolName}' must have a valid Type."
            });
        }

        return results;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Template {TemplateId} '{Name}' registered")]
    private partial void LogTemplateRegistered(TemplateId templateId, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Template {TemplateId} '{Name}' published")]
    private partial void LogTemplatePublished(TemplateId templateId, string name);
}
