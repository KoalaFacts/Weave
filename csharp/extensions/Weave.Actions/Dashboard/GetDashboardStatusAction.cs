using System.Globalization;
using Weave.Actions.Context;
using Weave.Actions.SystemInfo;
using Weave.Shared;

namespace Weave.Actions.Dashboard;

/// <summary>
/// Phase 1 read-only verb. Resolves the Weave dashboard URL (caller-supplied
/// or computed from <see cref="ISystemConfigSource"/> using the standard
/// silo-vs-dashboard port pairing) and probes its reachability with the
/// typed <see cref="HttpClient"/>. Browser-launching is a frontend concern
/// and stays in the CLI.
/// </summary>
public sealed class GetDashboardStatusAction
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly ISystemConfigSource _configSource;
    private readonly HttpClient _httpClient;

    public GetDashboardStatusAction(ISystemConfigSource configSource, HttpClient httpClient)
    {
        _configSource = configSource;
        _httpClient = httpClient;
    }

    public async Task<ActionResult<GetDashboardStatusResult>> ExecuteAsync(
        GetDashboardStatusInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var url = string.IsNullOrWhiteSpace(input.Url)
            ? DefaultUrl(_configSource.Load())
            : input.Url;

        var reachable = await ProbeAsync(url, cancellationToken);
        return ActionResult.Success(new GetDashboardStatusResult(url, reachable));
    }

    private static string DefaultUrl(SystemConfigSnapshot snapshot)
    {
        var port = snapshot.DefaultPort == WeavePorts.SiloHttp
            ? WeavePorts.DashboardHttp
            : snapshot.DefaultPort + 1;
        return $"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    private async Task<bool> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.IsSuccessStatusCode || (int)response.StatusCode < 500;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                       or TaskCanceledException
                                       or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
