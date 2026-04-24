using System.Text.Json;
using Weave.Agents.Channels;
using Weave.Agents.Models;

namespace Weave.Silo.Channels;

public sealed class TelegramChannelAdapter(HttpClient httpClient) : IChannelAdapter
{
    private const string TelegramApiBase = "https://api.telegram.org";

    public ChannelType Type => ChannelType.Telegram;

    public async Task SendAsync(OutboundMessage message, ChannelConfig config, CancellationToken ct)
    {
        if (!config.Config.TryGetValue("bot_token", out var botToken) || string.IsNullOrWhiteSpace(botToken))
            throw new InvalidOperationException("Telegram channel config must include 'bot_token'.");
        if (!config.Config.TryGetValue("chat_id", out var chatId) || string.IsNullOrWhiteSpace(chatId))
            throw new InvalidOperationException("Telegram channel config must include 'chat_id'.");

        var url = $"{TelegramApiBase}/bot{botToken}/sendMessage";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            chat_id = chatId,
            text = message.Content,
            reply_to_message_id = message.ThreadId
        });

        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await httpClient.PostAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Telegram returned {(int)response.StatusCode}: {errorBody}");
        }
    }

    public Task<bool> ValidateConfigAsync(Dictionary<string, string> config, CancellationToken ct)
    {
        var valid = config.ContainsKey("bot_token") && !string.IsNullOrWhiteSpace(config["bot_token"])
            && config.ContainsKey("chat_id") && !string.IsNullOrWhiteSpace(config["chat_id"]);
        return Task.FromResult(valid);
    }
}
