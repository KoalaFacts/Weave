using System.Text.Json;
using Weave.Agents.Channels;
using Weave.Agents.Models;

namespace Weave.Silo.Channels;

public sealed class DiscordChannelAdapter(HttpClient httpClient) : IChannelAdapter
{
    public ChannelType Type => ChannelType.Discord;

    public async Task SendAsync(OutboundMessage message, ChannelConfig config, CancellationToken ct)
    {
        if (!config.Config.TryGetValue("webhook_url", out var webhookUrl) || string.IsNullOrWhiteSpace(webhookUrl))
            throw new InvalidOperationException("Discord channel config must include 'webhook_url'.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { content = message.Content });

        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await httpClient.PostAsync(webhookUrl, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Discord returned {(int)response.StatusCode}: {errorBody}");
        }
    }

    public Task<bool> ValidateConfigAsync(Dictionary<string, string> config, CancellationToken ct)
    {
        var valid = config.ContainsKey("webhook_url") && !string.IsNullOrWhiteSpace(config["webhook_url"]);
        return Task.FromResult(valid);
    }
}
