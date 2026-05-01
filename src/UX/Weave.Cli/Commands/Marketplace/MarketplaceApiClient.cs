using System.Net.Http.Json;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public MarketplaceApiClient(string? baseUrl = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl ?? CliApiHttp.ResolveBaseUrl(), UriKind.Absolute)
        };
    }

    public async Task<IReadOnlyList<ApiMarketplaceItemResponse>> GetItemsAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("/api/marketplace", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiMarketplaceItemResponse, cancellationToken)) ?? [];
    }

    public async Task<IReadOnlyList<ApiMarketplaceItemResponse>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/marketplace/search?q={Uri.EscapeDataString(query)}", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiMarketplaceItemResponse, cancellationToken)) ?? [];
    }

    public async Task<ApiMarketplaceItemResponse?> GetItemAsync(string itemId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/marketplace/{Uri.EscapeDataString(itemId)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiMarketplaceItemResponse, cancellationToken);
    }

    public async Task<ApiMarketplaceItemResponse> SubmitItemAsync(
        string name, string description, string category, string version, string author, string[] tags,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/marketplace", new ApiSubmitMarketplaceRequest
        {
            Name = name,
            Description = description,
            Category = category,
            Version = version,
            Author = author,
            Tags = [.. tags]
        }, CliApiJsonContext.Default.ApiSubmitMarketplaceRequest, cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiMarketplaceItemResponse, cancellationToken))
            ?? throw new InvalidOperationException("Marketplace API returned an empty response.");
    }

    public async Task<ApiMarketplaceItemResponse> PublishItemAsync(
        string itemId, string reviewerId, bool approved, string? notes, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/marketplace/{Uri.EscapeDataString(itemId)}/publish", new ApiPublishMarketplaceRequest
        {
            ReviewerId = reviewerId,
            Approved = approved,
            Notes = notes
        }, CliApiJsonContext.Default.ApiPublishMarketplaceRequest, cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiMarketplaceItemResponse, cancellationToken))
            ?? throw new InvalidOperationException("Marketplace API returned an empty response.");
    }

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var response = await _httpClient.GetAsync("/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
