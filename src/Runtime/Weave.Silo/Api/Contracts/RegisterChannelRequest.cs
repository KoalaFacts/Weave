using System.Text.Json.Serialization;
using Weave.Agents.Channels;
namespace Weave.Silo.Api;

public sealed record RegisterChannelRequest
{
    [JsonConverter(typeof(JsonStringEnumConverter<ChannelType>))]
    public required ChannelType Type { get; init; }
    public required string Name { get; init; }
    public Dictionary<string, string>? Config { get; init; }
    public string? TargetAgent { get; init; }
}
