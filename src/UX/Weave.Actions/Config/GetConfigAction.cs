using Weave.Actions.Context;
using Weave.Actions.SystemInfo;

namespace Weave.Actions.Config;

/// <summary>
/// Phase 1 read-only verb. Returns the local CLI config snapshot
/// (<c>~/.weave/config.json</c> in the CLI binding) — purely client-side data,
/// so unlike most action-layer verbs there is no silo round-trip. Optional
/// single-key lookup mirrors <c>weave config get [key]</c>.
/// </summary>
public sealed class GetConfigAction
{
    private readonly ISystemConfigSource _configSource;

    public GetConfigAction(ISystemConfigSource configSource)
    {
        _configSource = configSource;
    }

    public Task<ActionResult<GetConfigResult>> ExecuteAsync(
        GetConfigInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = _configSource.Load();
        var summary = ToSummary(snapshot);

        if (input.Key is null)
        {
            return Task.FromResult(ActionResult.Success(new GetConfigResult { Summary = summary }));
        }

        var value = LookupValue(summary, input.Key);
        if (value is null)
        {
            return Task.FromResult(ActionResult.Failed<GetConfigResult>(
                ActionFailure.ValidationFailed(
                    $"Unknown config key '{input.Key}'. Valid keys: {string.Join(", ", ConfigKeys.All)}.")));
        }

        return Task.FromResult(ActionResult.Success(new GetConfigResult
        {
            Summary = summary,
            RequestedValue = value
        }));
    }

    private static ConfigSummary ToSummary(SystemConfigSnapshot snapshot) => new()
    {
        Version = snapshot.Version,
        DefaultPort = snapshot.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Storage = snapshot.Storage,
        AuthMode = snapshot.AuthMode,
        RequireHttps = snapshot.RequireHttps ? "true" : "false",
        SiloPath = snapshot.SiloPath ?? "(not set)",
        WeaveHome = snapshot.WeaveHome,
        BaseUrl = snapshot.BaseUrl
    };

    private static string? LookupValue(ConfigSummary summary, string key) => key.ToLowerInvariant() switch
    {
        "version" => summary.Version,
        "defaultport" => summary.DefaultPort,
        "storage" => summary.Storage,
        "authmode" => summary.AuthMode,
        "requirehttps" => summary.RequireHttps,
        "silopath" => summary.SiloPath,
        "weavehome" => summary.WeaveHome,
        "baseurl" => summary.BaseUrl,
        _ => null
    };
}
