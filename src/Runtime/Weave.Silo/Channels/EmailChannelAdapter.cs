using System.Text.Json;
using Weave.Agents.Channels;
using Weave.Agents.Models;

namespace Weave.Silo.Channels;

public sealed class EmailChannelAdapter(HttpClient httpClient) : IChannelAdapter
{
    public ChannelType Type => ChannelType.Email;

    public async Task SendAsync(OutboundMessage message, ChannelConfig config, CancellationToken ct)
    {
        if (!config.Config.TryGetValue("relay_url", out var relayUrl) || string.IsNullOrWhiteSpace(relayUrl))
            throw new InvalidOperationException("Email channel config must include 'relay_url'.");
        if (!config.Config.TryGetValue("to", out var to) || string.IsNullOrWhiteSpace(to))
            throw new InvalidOperationException("Email channel config must include 'to'.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new EmailPayload(
                to,
                config.Config.GetValueOrDefault("subject", "Weave Agent Response"),
                message.Content),
            ChannelPayloadJsonContext.Default.EmailPayload);

        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await httpClient.PostAsync(relayUrl, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Email relay returned {(int)response.StatusCode}: {errorBody}");
        }
    }

    public Task<bool> ValidateConfigAsync(Dictionary<string, string> config, CancellationToken ct)
    {
        var valid = config.ContainsKey("relay_url") && !string.IsNullOrWhiteSpace(config["relay_url"])
            && config.ContainsKey("to") && !string.IsNullOrWhiteSpace(config["to"]);
        return Task.FromResult(valid);
    }
}
