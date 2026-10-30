using System.Text.Json.Serialization;
using Weave.Agents.Channels;
namespace Weave.Silo.Api;

public sealed record InboundMessageRequest
{
    public required string ChannelId { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<ChannelType>))]
    public required ChannelType SourceChannel { get; init; }
    public required string SenderId { get; init; }
    public string SenderName { get; init; } = string.Empty;
    public required string Content { get; init; }
    public string? ThreadId { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
