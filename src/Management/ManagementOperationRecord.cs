using System.Text.Json.Serialization;

namespace Weave.Management;

[JsonConverter(typeof(JsonStringEnumConverter<ManagementOperationOutcome>))]
public enum ManagementOperationOutcome
{
    OutcomeUnknown,
    Succeeded,
    Failed
}

public sealed record ManagementOperationRecord
{
    public required string Id { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Subject { get; init; }
    public required string TokenId { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public required string AuthorizedGrants { get; init; }
    public required string RequestDigest { get; init; }
    public required DateTimeOffset AdmittedAt { get; init; }
    public ManagementOperationOutcome Outcome { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
