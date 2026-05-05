using Weave.Actions.Context;

namespace Weave.Actions.System;

/// <summary>
/// Pilot action for the Shape C migration. Reads the local config snapshot via
/// <see cref="ISystemConfigSource"/>, probes the silo via <see cref="ISiloProbe"/>,
/// and returns the combined <see cref="SystemInfoResult"/>. Frontend-agnostic: no
/// console writes, no Spectre, no decision about how to render.
/// </summary>
public sealed class GetSystemInfoAction
{
    private readonly ISystemConfigSource _configSource;
    private readonly ISiloProbe _siloProbe;

    public GetSystemInfoAction(ISystemConfigSource configSource, ISiloProbe siloProbe)
    {
        _configSource = configSource;
        _siloProbe = siloProbe;
    }

    public async Task<ActionResult<SystemInfoResult>> ExecuteAsync(
        GetSystemInfoInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var snapshot = _configSource.Load();
        var reachable = await _siloProbe.IsReachableAsync(cancellationToken);

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
}
