using System.Text.Json.Serialization;

namespace Weave.Actions.Audit;

internal sealed record CapabilityAuditEntryWire
{
    public string TokenId { get; init; } = string.Empty;
    public string Grant { get; init; } = string.Empty;
    public string IssuedTo { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string ActionContext { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CapabilityAuditEntryWire))]
[JsonSerializable(typeof(List<CapabilityAuditEntryWire>))]
[JsonSerializable(typeof(CapabilityAuditEntryWire[]))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;
