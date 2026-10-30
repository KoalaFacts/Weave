using System.Text.Json;
using Weave.Agents.Channels;
namespace Weave.Silo.Channels;

public sealed class SlackChannelAdapter(HttpClient httpClient) : IChannelAdapter
{
    public ChannelType Type => ChannelType.Slack;

    public async Task SendAsync(OutboundMessage message, ChannelConfig config, CancellationToken ct)
    {
        if (!config.Config.TryGetValue("webhook_url", out var webhookUrl) || string.IsNullOrWhiteSpace(webhookUrl))
            throw new InvalidOperationException("Slack channel config must include 'webhook_url'.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new SlackPayload(message.Content, message.ThreadId),
            ChannelPayloadJsonContext.Default.SlackPayload);

        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await httpClient.PostAsync(webhookUrl, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Slack returned {(int)response.StatusCode}: {errorBody}");
        }
    }

    public Task<bool> ValidateConfigAsync(Dictionary<string, string> config, CancellationToken ct)
    {
        var valid = config.ContainsKey("webhook_url") && !string.IsNullOrWhiteSpace(config["webhook_url"]);
        return Task.FromResult(valid);
    }
}
