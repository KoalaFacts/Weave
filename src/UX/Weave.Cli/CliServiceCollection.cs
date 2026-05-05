using Microsoft.Extensions.DependencyInjection;
using Weave.Actions;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.System;
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

        // TUI views that talk to the workspace API. WorkspaceApiClient is
        // singleton — Microsoft's HttpClient guidance is one-per-application,
        // not one-per-call; the prior `using var client = new WorkspaceApiClient();`
        // pattern in these views violated that. The two views consume the singleton
        // via primary-ctor injection. The other 13 inline `new WorkspaceApiClient()`
        // call sites in the CLI remain — see the Shape C plan in docs/handoff.md.
        services.AddSingleton<WorkspaceApiClient>();
        services.AddTransient<TuiToolsView>();
        services.AddTransient<TuiTasksView>();

        // Shape C action context + actions. WorkspaceApiClient implements
        // ISiloApi so actions depend on the seam, not the concrete client;
        // ISiloApi grows verb-by-verb as Phase 1 read-only verbs land. The
        // legacy /system slash in the TUI still uses TuiSystemView until its
        // own migration; the new `weave agents` CLI surface and the
        // migrated /agents slash are the first cross-frontend consumers.
        services.AddSingleton<IActionPrompter, ConsoleActionPrompter>();
        services.AddSingleton<IActionReporter, ConsoleActionReporter>();
        services.AddSingleton<ISiloApi>(sp => sp.GetRequiredService<WorkspaceApiClient>());
        services.AddSingleton<ISystemConfigSource, CliSystemConfigSource>();
        services.AddTransient<GetSystemInfoAction>();
        services.AddTransient<ListAgentsAction>();
        services.AddTransient<SystemCliCommand>();
        services.AddTransient<AgentsCliCommand>();

        return services.BuildServiceProvider();
    }
}
