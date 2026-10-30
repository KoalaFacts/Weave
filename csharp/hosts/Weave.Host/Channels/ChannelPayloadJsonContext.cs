using System.Text.Json.Serialization;

namespace Weave.Silo.Channels;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TeamsPayload))]
[JsonSerializable(typeof(DiscordPayload))]
[JsonSerializable(typeof(SlackPayload))]
[JsonSerializable(typeof(TelegramPayload))]
[JsonSerializable(typeof(EmailPayload))]
internal sealed partial class ChannelPayloadJsonContext : JsonSerializerContext;
