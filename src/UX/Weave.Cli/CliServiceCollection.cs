using Microsoft.Extensions.DependencyInjection;
using Weave.Actions;
using Weave.Actions.Context;
using Weave.Actions.SystemInfo;
using Weave.Cli.Commands;
using Weave.Cli.Tui;

namespace Weave.Cli;

/// <summary>
/// Builds the CLI's <see cref="IServiceProvider"/>. Phase 3.5 promoted the
/// pre-existing CLI shell singletons (config store, workspace registry,
/// manifest resolver, silo launcher, secret resolver) from static helpers to
/// DI-resolved interface implementations, so every CLI command handler now
/// lives in DI and constructor-injects what it needs.
/// </summary>
internal static class CliServiceCollection
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton(TimeProvider.System);

        // Shell primitives — file-backed and process-shaped services that used
        // to be `internal static class`. One singleton each; no per-call state.
        services.AddSingleton<IConfigStore, CliConfigStore>();
        services.AddSingleton<ISecretResolver, CliSecretResolver>();
        services.AddSingleton<IWorkspaceRegistry, WorkspaceRegistry>();
        services.AddSingleton<IManifestResolver, ManifestResolver>();
        services.AddSingleton<ISiloLauncher, SiloLauncher>();
        services.AddSingleton<WorkspacePrompt>();
        services.AddSingleton<WorkspaceCompletions>();
        services.AddSingleton<SiloProcessService>();

        // VersionService — time-dependent; instance class registered as singleton
        // so its in-memory cache (such as it has) survives across calls.
        services.AddSingleton<VersionService>();

        // CLI command handlers — every *CliCommand resolved from DI; the
        // System.CommandLine factory `XCommand.Create(handler, ...)` accepts
        // each one as a parameter wired up in Program.cs.
        services.AddTransient<UpgradeCliCommand>();
        services.AddTransient<TuiCliCommand>();
        services.AddTransient<InitCliCommand>();
        services.AddTransient<RunCliCommand>();
        services.AddTransient<ServeCliCommand>();
        services.AddTransient<PortsCliCommand>();
        services.AddTransient<VersionCliCommand>();
        services.AddTransient<WorkspaceListCliCommand>();
        services.AddTransient<WorkspaceRemoveCliCommand>();
        services.AddTransient<WorkspaceNewCliCommand>();
        services.AddTransient<WorkspaceShowCliCommand>();
        services.AddTransient<WorkspaceAddAgentCliCommand>();
        services.AddTransient<WorkspaceAddToolCliCommand>();
        services.AddTransient<WorkspaceAddTargetCliCommand>();
        services.AddTransient<WorkspaceAddPluginCliCommand>();
        services.AddTransient<WorkspacePluginListCliCommand>();
        services.AddTransient<WorkspacePluginRemoveCliCommand>();
        services.AddTransient<WorkspacePublishCliCommand>();
        services.AddTransient<WorkspacePresetsCliCommand>();
        services.AddTransient<WorkspaceStorageShowCliCommand>();
        services.AddTransient<WorkspaceStorageChangeCliCommand>();
        services.AddTransient<StorageShowCliCommand>();
        services.AddTransient<StorageChangeCliCommand>();
        services.AddTransient<DataExportCliCommand>();
        services.AddTransient<DataImportCliCommand>();
        services.AddTransient<MarketplaceListCliCommand>();
        services.AddTransient<MarketplaceSearchCliCommand>();
        services.AddTransient<MarketplaceSubmitCliCommand>();
        services.AddTransient<MarketplacePublishCliCommand>();
        services.AddTransient<MarketplaceInfoCliCommand>();
        services.AddTransient<MarketplaceInstallCliCommand>();
        services.AddTransient<AuditReplayCliCommand>();

        // TUI composer chain — Transient because each TuiShell builds its own.
        services.AddTransient<ChatExitConfirmation>();
        services.AddTransient<ChatComposerEditor>();
        services.AddTransient<ChatComposer>();
        services.AddTransient<TuiShell>();
        services.AddTransient<TuiAgentNameSource>();
        services.AddTransient<TuiAgentSelector>();
        services.AddTransient<TuiChatSession>();

        // Migrated TUI views — consume actions through DI.
        services.AddTransient<TuiToolsView>();
        services.AddTransient<TuiTasksView>();
        services.AddTransient<TuiConfigView>();
        services.AddTransient<TuiSystemView>();
        services.AddTransient<TuiWorkspaceDashboard>();
        services.AddTransient<TuiLiveStatusWatcher>();
        services.AddTransient<TuiLiveStatusView>();

        // Shape C action layer — frontend-supplied prompter/reporter, plus
        // typed HttpClients per action keyed off the silo base URL.
        services.AddSingleton<IActionPrompter, ConsoleActionPrompter>();
        services.AddSingleton<IActionReporter, ConsoleActionReporter>();
        services.AddSingleton<ISystemConfigSource, CliSystemConfigSource>();
        services.AddSingleton<Weave.Actions.Config.ISystemConfigWriter, CliSystemConfigWriter>();
        services.AddSingleton<Weave.Actions.Workspace.IWorkspaceLocator, CliWorkspaceLocator>();
        services.AddSiloActions(client => client.BaseAddress = new Uri(CliApiHttp.ResolveBaseUrl()));
        services.AddTransient<SystemCliCommand>();
        services.AddTransient<AgentsCliCommand>();
        services.AddTransient<ToolsCliCommand>();
        services.AddTransient<TasksCliCommand>();
        services.AddTransient<WorkspaceStatusCliCommand>();
        services.AddTransient<WorkspaceValidateCliCommand>();
        services.AddTransient<WorkspaceUpCliCommand>();
        services.AddTransient<WorkspaceDownCliCommand>();
        services.AddTransient<IWorkspaceDownDependencies, DefaultWorkspaceDownDependencies>();
        services.AddTransient<ConfigGetCliCommand>();
        services.AddTransient<ConfigSetCliCommand>();
        services.AddTransient<WebUiCliCommand>();

        return services.BuildServiceProvider();
    }
}
