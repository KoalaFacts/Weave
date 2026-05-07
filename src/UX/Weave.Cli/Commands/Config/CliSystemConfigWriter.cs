using System.Globalization;
using Weave.Actions.Config;

namespace Weave.Cli.Commands;

/// <summary>
/// CLI binding for <see cref="ISystemConfigWriter"/>: applies a validated
/// key/value pair to <see cref="CliConfigStore"/>. The action layer has
/// already verified the key is in <see cref="ConfigKeys.Writable"/> and
/// parsed any value-type constraints, so this implementation just dispatches
/// onto the matching <see cref="CliConfig"/> field and saves.
/// </summary>
internal sealed class CliSystemConfigWriter : ISystemConfigWriter
{
    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = CliConfigStore.Load();
        var updated = key switch
        {
            "siloPath" => config with { SiloPath = value },
            "defaultPort" => config with { DefaultPort = int.Parse(value, CultureInfo.InvariantCulture) },
            _ => throw new ArgumentException($"CliSystemConfigWriter received unwritable key '{key}'.", nameof(key))
        };

        CliConfigStore.Save(updated);
        return Task.CompletedTask;
    }
}
