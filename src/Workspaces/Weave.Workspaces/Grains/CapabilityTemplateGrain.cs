using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Grains;

public sealed partial class CapabilityTemplateGrain(
    ILogger<CapabilityTemplateGrain> logger,
    [PersistentState("capability-templates", "Default")] IPersistentState<TemplateRegistryState> persistentState)
    : Grain, ICapabilityTemplateGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
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
            template.PublishedAt = DateTimeOffset.UtcNow;
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
        IReadOnlyList<CapabilityTemplate> published = [.. persistentState.State.Templates.Values
            .Where(t => t.Status == TemplateStatus.Published)
            .OrderByDescending(t => t.PublishedAt)
            .Skip(offset)
            .Take(limit)];
        return Task.FromResult(published);
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

        IReadOnlyList<CapabilityTemplate> results = [.. candidates.Take(maxResults)];
        return Task.FromResult(results);
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

        foreach (var toolRef in template.AgentDefinition.Tools)
        {
            var found = template.RequiredTools.ContainsKey(toolRef);
            results.Add(new TemplateValidationResult
            {
                Check = $"ToolPresent:{toolRef}",
                Passed = found,
                Detail = found ? null : $"Tool '{toolRef}' is referenced by the agent but not present in RequiredTools."
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
