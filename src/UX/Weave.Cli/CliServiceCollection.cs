using Microsoft.Extensions.DependencyInjection;
using Weave.Actions;
using Weave.Actions.Context;
using Weave.Actions.SystemInfo;
using Weave.Cli.ActionContext;
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

        // Migrated TUI views — consume actions through DI (no shared
        // WorkspaceApiClient seam). The remaining inline `new
        // WorkspaceApiClient()` callsites drain as their consumers move into
        // actions in Phases 1-2.
        services.AddTransient<TuiToolsView>();
        services.AddTransient<TuiTasksView>();
        services.AddTransient<TuiConfigView>();
        services.AddTransient<TuiLiveStatusWatcher>();
        services.AddTransient<TuiLiveStatusView>();

        // Shape C action layer — frontend-supplied prompter/reporter, plus
        // typed HttpClients per action keyed off the silo base URL. Actions
        // own their HTTP and DTOs; no shared silo-client abstraction.
        services.AddSingleton<IActionPrompter, ConsoleActionPrompter>();
        services.AddSingleton<IActionReporter, ConsoleActionReporter>();
        services.AddSingleton<ISystemConfigSource, CliSystemConfigSource>();
        services.AddSiloActions(client => client.BaseAddress = new Uri(CliApiHttp.ResolveBaseUrl()));
        services.AddTransient<SystemCliCommand>();
        services.AddTransient<AgentsCliCommand>();
        services.AddTransient<ToolsCliCommand>();
        services.AddTransient<TasksCliCommand>();
        services.AddTransient<WorkspaceStatusCliCommand>();
        services.AddTransient<WorkspaceValidateCliCommand>();
        services.AddTransient<ConfigGetCliCommand>();

        return services.BuildServiceProvider();
    }
}
