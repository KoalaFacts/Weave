using Microsoft.Extensions.DependencyInjection;
using Weave.Cli.Commands;
using Weave.Cli.Tui;

namespace Weave.Cli;

/// <summary>
/// Builds the CLI's <see cref="IServiceProvider"/>. Today scoped to the
/// services that the TUI chain (<see cref="TuiShell"/>, <see cref="ChatComposer"/>,
/// <see cref="ChatExitConfirmation"/>) and time-dependent CLI commands
/// (<see cref="VersionService"/>) need.
///
/// CLI commands not yet on DI keep using their existing static-factory
/// pattern — adding them here as they get cleaned up is the migration path.
/// </summary>
internal static class CliServiceCollection
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton(TimeProvider.System);

        // VersionService — time-dependent; instance class registered as singleton
        // so its in-memory cache (such as it has) survives across calls.
        services.AddSingleton<VersionService>();

        // CLI commands that take dependencies; the rest still use their static
        // factory pattern and are added here as they get migrated.
        services.AddTransient<UpgradeCliCommand>();
        services.AddTransient<TuiCliCommand>();

        // TUI composer chain — Transient because each TuiShell builds its own.
        services.AddTransient<ChatExitConfirmation>();
        services.AddTransient<ChatComposerEditor>();
        services.AddTransient<ChatComposer>();
        services.AddTransient<TuiShell>();

        return services.BuildServiceProvider();
    }
}
