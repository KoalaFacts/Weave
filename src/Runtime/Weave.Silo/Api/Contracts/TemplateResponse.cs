using System.Text.Json.Serialization;
using Weave.Workspaces.Models;

namespace Weave.Silo.Api;

public sealed record TemplateResponse
{
    public required string TemplateId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<TemplateStatus>))]
    public required TemplateStatus Status { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public List<string> Tags { get; init; } = [];
    public int InstantiationCount { get; init; }
    public List<TemplateValidationResultResponse> ValidationResults { get; init; } = [];

    public static TemplateResponse FromTemplate(CapabilityTemplate t) => new()
    {
        TemplateId = t.TemplateId.ToString(),
        Name = t.Name,
        Description = t.Description,
        Version = t.Version,
        Author = t.Author,
        Status = t.Status,
        PublishedAt = t.PublishedAt,
        Tags = [.. t.Tags],
        InstantiationCount = t.InstantiationCount,
        ValidationResults = t.ValidationResults.Select(r => new TemplateValidationResultResponse
        {
            Check = r.Check,
            Passed = r.Passed,
            Detail = r.Detail
        }).ToList()
    };
}
