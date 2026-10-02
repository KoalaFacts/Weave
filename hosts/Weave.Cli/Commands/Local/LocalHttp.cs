using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Weave.Cli.Commands.Local;

internal sealed class LocalHttp(HttpClient client, TimeProvider clock)
{
    public const int MaxBytes = 1_048_576;

    public static HttpClient CreateClient(string origin)
    {
        var uri = new Uri(origin);
        if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("This local client requires a literal loopback HTTP origin without credentials, path or query.");
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        {
            BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<LocalHttpResult> CallAsync(HttpMethod method, string route, string? capability,
        string? operatorKey, JsonObject? body, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = scope.Token;
        using var request = CreateRequest(method, route, capability, operatorKey);
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            if (bytes.Length > MaxBytes)
                throw new ArgumentException("Proposal exceeds the one MiB client limit.");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var data = await ReadAsync(response, ct);
        return new LocalHttpResult((int)response.StatusCode, data.Length == 0 ? null : JsonNode.Parse(data));
    }

    public async Task<string> IssueAsync(string profile, string operatorKey, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = scope.Token;
        using var request = CreateRequest(HttpMethod.Post, $"/api/operator/credentials/{profile}/issue", null, operatorKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var data = await ReadAsync(response, ct);
        if ((int)response.StatusCode != 200 || response.Content.Headers.ContentType?.MediaType != "text/plain"
            || response.Headers.CacheControl?.NoStore != true || data.Length == 0)
            throw new HttpRequestException("Credential issuance was not confirmed. No credential was delivered.");
        var capability = Encoding.UTF8.GetString(data).Trim();
        if (capability.Length == 0)
            throw new HttpRequestException("Credential issuance returned an empty credential.");
        return capability;
    }

    public async Task ConnectDocumentsAsync(string operatorKey, CancellationToken ct)
    {
        var connected = await CallAsync(HttpMethod.Post, "/api/operator/tools/files/connect", null, operatorKey, null, ct);
        if (connected.Status != 204)
            throw new HttpRequestException("The configured document tool connection was not confirmed. No Agent was started.");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string route, string? capability, string? operatorKey)
    {
        if (!route.StartsWith("/api/", StringComparison.Ordinal) || route.Contains('?') || route.Contains('#'))
            throw new ArgumentException("Unexpected local API route.");
        var request = new HttpRequestMessage(method, route);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (capability is not null)
            request.Headers.Add("X-Weave-Capability", capability);
        if (operatorKey is not null)
            request.Headers.Add("X-Weave-Operator-Key", operatorKey);
        return request;
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxBytes)
            throw new HttpRequestException("Local API response exceeds the client limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0)
                return output.ToArray();
            if (output.Length + count > MaxBytes)
                throw new HttpRequestException("Local API response exceeds the client limit.");
            output.Write(buffer, 0, count);
        }
    }
}
