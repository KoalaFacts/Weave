using System.Text.Json.Serialization;
using Weave.Agents.Verification;
namespace Weave.Silo.Api;

public sealed record ProofItemResponse
{
    [JsonConverter(typeof(JsonStringEnumConverter<ProofType>))]
    public required ProofType Type { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Uri { get; init; }

    public static ProofItemResponse FromItem(ProofItem item) => new()
    {
        Type = item.Type,
        Label = item.Label,
        Value = item.Value,
        Uri = item.Uri
    };
}
