using System.Text.Json;
using Weave.Shared;

namespace Weave.Cli.Commands;

internal static class CliApiHttp
{
    public static string ResolveBaseUrl() =>
        Environment.GetEnvironmentVariable("WEAVE_API_URL") ?? $"http://localhost:{WeavePorts.SiloHttp}";

    public static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body;
        try
        { body = await response.Content.ReadAsStringAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { body = string.Empty; }

        var message = FormatHttpError((int)response.StatusCode, response.ReasonPhrase, body);
        throw new HttpRequestException(message, inner: null, response.StatusCode);
    }

    private static string FormatHttpError(int statusCode, string? reason, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return $"HTTP {statusCode} {reason}";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.GetString() ?? body.Trim();
            if (doc.RootElement.TryGetProperty("title", out var title))
                return title.GetString() ?? body.Trim();
        }
        catch (JsonException)
        {
            // Body is not RFC 7807 JSON — fall through to raw text below.
        }

        return body.Trim();
    }
}
