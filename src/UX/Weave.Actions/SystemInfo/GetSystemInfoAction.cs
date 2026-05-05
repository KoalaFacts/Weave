using Weave.Actions.Context;

namespace Weave.Actions.SystemInfo;

/// <summary>
/// Pilot action for the Shape C migration. Reads the local config snapshot via
/// <see cref="ISystemConfigSource"/>, probes the silo's <c>/health</c> via
/// the typed <see cref="HttpClient"/>, and returns the combined
/// <see cref="SystemInfoResult"/>. Frontend-agnostic: no console writes,
/// no Spectre, no decision about how to render.
/// </summary>
public sealed class GetSystemInfoAction
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly ISystemConfigSource _configSource;
    private readonly HttpClient _httpClient;

    public GetSystemInfoAction(ISystemConfigSource configSource, HttpClient httpClient)
    {
        _configSource = configSource;
        _httpClient = httpClient;
    }

    public async Task<ActionResult<SystemInfoResult>> ExecuteAsync(
        GetSystemInfoInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var snapshot = _configSource.Load();
        var reachable = await ProbeAsync(cancellationToken);

        return ActionResult.Success(new SystemInfoResult
        {
            Reachable = reachable,
            BaseUrl = snapshot.BaseUrl,
            DefaultPort = snapshot.DefaultPort,
            Storage = snapshot.Storage,
            AuthMode = snapshot.AuthMode,
            RequireHttps = snapshot.RequireHttps,
            SiloPath = snapshot.SiloPath,
            WeaveHome = snapshot.WeaveHome
        });
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            var response = await _httpClient.GetAsync("/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                       or TaskCanceledException
                                       or global::System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
