using Microsoft.Extensions.DependencyInjection;
using Weave.Actions;
using Weave.Actions.Context;
using Weave.Actions.SystemInfo;
using Weave.Cli.Commands;
using Weave.Cli.Tui;
using Weave.Cli.Tui.Verbs;

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
        services.AddTransient<TuiSlashCommandDispatcher>();
        services.AddTransient<TuiAgentNameSource>();
        services.AddTransient<TuiChatSession>();
        services.AddTransient<TuiLiveStatusWatcher>();
        services.AddTransient<TuiLiveStatusView>();

        // TUI helpers also referenced by other helpers (not just as verbs).
        services.AddTransient<TuiAgentSelector>();
        services.AddTransient<TuiWorkspaceDashboard>();

        // ITuiVerb registry. Helpers that already do real per-verb work
        // implement ITuiVerb directly; bare ICliCommand handlers get a thin
        // wrapper. Phase 4a's verb registry collapses the dispatcher's 19
        // ctor seams to 2 (TuiChatSession + IEnumerable<ITuiVerb>).
        // TuiAgentSelector and TuiWorkspaceDashboard are already concretely
        // registered above; alias each as ITuiVerb so the dispatcher picks them
        // up from IEnumerable<ITuiVerb>. RefreshVerb is a thin wrapper around
        // the dashboard so /refresh and the startup refresh share state.
        services.AddTransient<ITuiVerb>(sp => sp.GetRequiredService<TuiAgentSelector>());
        services.AddTransient<ITuiVerb, RefreshVerb>();
        services.AddTransient<ITuiVerb, TuiAgentListView>();
        services.AddTransient<ITuiVerb, TuiWorkspaceOpener>();
        services.AddTransient<ITuiVerb, TuiWorkspaceStarter>();
        services.AddTransient<ITuiVerb, TuiWorkspaceWatcher>();
        services.AddTransient<ITuiVerb, TuiToolsView>();
        services.AddTransient<ITuiVerb, TuiTasksView>();
        services.AddTransient<ITuiVerb, TuiConfigView>();
        services.AddTransient<ITuiVerb, TuiSystemView>();
        services.AddTransient<ITuiVerb, StatusVerb>();
        services.AddTransient<ITuiVerb, ValidateVerb>();
        services.AddTransient<ITuiVerb, DownVerb>();
        services.AddTransient<ITuiVerb, WebUiVerb>();
        services.AddTransient<ITuiVerb, UpgradeVerb>();
        services.AddTransient<ITuiVerb, PortsVerb>();
        services.AddTransient<ITuiVerb, VersionVerb>();
        services.AddTransient<ITuiVerb, PresetsVerb>();

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
