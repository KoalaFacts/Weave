using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Models;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

namespace Weave.Silo.Templates;

/// <summary>
/// Seeds <see cref="BuiltInTemplates.All"/> into the runtime template registry
/// on silo startup. Idempotent — every entry already present is skipped, so
/// re-runs and persistent backends are safe.
/// </summary>
public sealed partial class BuiltInTemplateSeeder(
    IVirtualActorProvider actors,
    ILogger<BuiltInTemplateSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var actor = actors.GetActor<ICapabilityTemplateActor>(VirtualActorId.From("global"));

        foreach (var template in BuiltInTemplates.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = await actor.GetAsync(template.TemplateId);
            if (existing is not null)
            {
                LogSkipped(template.TemplateId, template.Name);
                continue;
            }

            await actor.RegisterAsync(template);
            var published = await actor.ValidateAndPublishAsync(template.TemplateId);
            if (published.Status != TemplateStatus.Published)
            {
                LogValidationFailed(template.TemplateId, template.Name,
                    string.Join("; ", published.ValidationResults
                        .Where(r => !r.Passed)
                        .Select(r => $"{r.Check}: {r.Detail ?? "(no detail)"}")));
                continue;
            }

            LogPublished(template.TemplateId, template.Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Built-in template '{TemplateId}' ({Name}) seeded and published")]
    private partial void LogPublished(TemplateId templateId, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Built-in template '{TemplateId}' ({Name}) already present; skipping")]
    private partial void LogSkipped(TemplateId templateId, string name);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Built-in template '{TemplateId}' ({Name}) failed validation and was not published. Failures: {Failures}")]
    private partial void LogValidationFailed(TemplateId templateId, string name, string failures);
}
