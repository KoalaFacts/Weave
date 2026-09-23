using System.Text.Json.Serialization;
using Weave.Agents.Channels;
namespace Weave.Silo.Api;

public sealed record ChannelResponse
{
    public required string ChannelId { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<ChannelType>))]
    public required ChannelType Type { get; init; }
    public required string Name { get; init; }
    public string? TargetAgent { get; init; }
    public required bool Enabled { get; init; }

    public static ChannelResponse FromConfig(ChannelConfig config) => new()
    {
        ChannelId = config.ChannelId.ToString(),
        Type = config.Type,
        Name = config.Name,
        TargetAgent = config.TargetAgent,
        Enabled = config.Enabled
    };
}
