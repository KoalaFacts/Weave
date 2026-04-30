using System.Text.Json.Serialization;
using Weave.Agents.Models;

namespace Weave.Silo.Api;

public sealed record ProofItemRequest
{
    [JsonConverter(typeof(JsonStringEnumConverter<ProofType>))]
    public required ProofType Type { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Uri { get; init; }
}
