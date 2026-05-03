using System.Net.Http.Json;

namespace Weave.Cli.Commands;

internal sealed class AuditApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public AuditApiClient(string? baseUrl = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl ?? CliApiHttp.ResolveBaseUrl(), UriKind.Absolute)
        };
    }

    public async Task<IReadOnlyList<ApiCapabilityAuditEntry>> GetByTokenAsync(string tokenId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/audit/capability/{Uri.EscapeDataString(tokenId)}", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiCapabilityAuditEntry, cancellationToken))
            ?? throw new InvalidOperationException("Audit API returned an empty payload.");
    }

    public async Task<IReadOnlyList<ApiCapabilityAuditEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/audit/capability?limit={limit}", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiCapabilityAuditEntry, cancellationToken))
            ?? throw new InvalidOperationException("Audit API returned an empty payload.");
    }

    public void Dispose() => _httpClient.Dispose();
}
