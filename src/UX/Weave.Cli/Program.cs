using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Weave.Cli;
using Weave.Cli.Commands;
using Weave.Cli.Shell;
using Weave.Cli.Tui;

var services = CliServiceCollection.Build();

// Zero-args, interactive terminal → launch the TUI.
if (args.Length == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected)
{
    using var tuiCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        tuiCts.Cancel();
    };

    return await TuiApp.RunAsync(services, tuiCts.Token);
}

var configStore = services.GetRequiredService<IConfigStore>();
var completions = services.GetRequiredService<WorkspaceCompletions>();

var root = new RootCommand("weave — set up AI assistants with guardrails you control.");

var workspace = new Command("workspace", "Manage workspaces");
root.Subcommands.Add(workspace);

workspace.Subcommands.Add(WorkspaceNewCommand.Create(services.GetRequiredService<WorkspaceNewCliCommand>()));
workspace.Subcommands.Add(WorkspaceListCommand.Create(services.GetRequiredService<WorkspaceListCliCommand>()));
workspace.Subcommands.Add(WorkspaceRemoveCommand.Create(services.GetRequiredService<WorkspaceRemoveCliCommand>(), completions));
workspace.Subcommands.Add(WorkspaceUpCommand.Create(services.GetRequiredService<WorkspaceUpCliCommand>(), completions));
workspace.Subcommands.Add(WorkspaceDownCommand.Create(services.GetRequiredService<WorkspaceDownCliCommand>(), completions));
workspace.Subcommands.Add(WorkspaceStatusCommand.Create(services.GetRequiredService<WorkspaceStatusCliCommand>(), completions));
workspace.Subcommands.Add(WorkspaceShowCommand.Create(services.GetRequiredService<WorkspaceShowCliCommand>(), completions));
workspace.Subcommands.Add(WorkspaceValidateCommand.Create(services.GetRequiredService<WorkspaceValidateCliCommand>(), completions));
workspace.Subcommands.Add(WorkspacePublishCommand.Create(services.GetRequiredService<WorkspacePublishCliCommand>(), completions));
workspace.Subcommands.Add(WorkspacePresetsCommand.Create(services.GetRequiredService<WorkspacePresetsCliCommand>()));

var add = new Command("add", "Add components to a workspace");
add.Subcommands.Add(WorkspaceAddAgentCommand.Create(services.GetRequiredService<WorkspaceAddAgentCliCommand>(), completions));
add.Subcommands.Add(WorkspaceAddToolCommand.Create(services.GetRequiredService<WorkspaceAddToolCliCommand>(), completions));
add.Subcommands.Add(WorkspaceAddTargetCommand.Create(services.GetRequiredService<WorkspaceAddTargetCliCommand>(), completions));
add.Subcommands.Add(WorkspaceAddPluginCommand.Create(services.GetRequiredService<WorkspaceAddPluginCliCommand>(), completions));
workspace.Subcommands.Add(add);

workspace.Subcommands.Add(WorkspaceStorageCommands.Create(
    services.GetRequiredService<WorkspaceStorageShowCliCommand>(),
    services.GetRequiredService<WorkspaceStorageChangeCliCommand>(),
    completions));

var plugin = new Command("plugin", "Manage workspace plugins");
plugin.Subcommands.Add(WorkspacePluginListCommand.Create(services.GetRequiredService<WorkspacePluginListCliCommand>(), completions));
plugin.Subcommands.Add(WorkspaceAddPluginCommand.Create(services.GetRequiredService<WorkspaceAddPluginCliCommand>(), completions));
plugin.Subcommands.Add(WorkspacePluginRemoveCommand.Create(services.GetRequiredService<WorkspacePluginRemoveCliCommand>(), completions));
workspace.Subcommands.Add(plugin);

root.Subcommands.Add(WorkspaceServeCommand.Create(services.GetRequiredService<ServeCliCommand>(), configStore));
root.Subcommands.Add(RunCommand.Create(services.GetRequiredService<RunCliCommand>(), configStore));
root.Subcommands.Add(TuiCommand.Create(services.GetRequiredService<TuiCliCommand>()));
root.Subcommands.Add(WebUiCommand.Create(services.GetRequiredService<WebUiCliCommand>()));
root.Subcommands.Add(InitCommand.Create(services.GetRequiredService<InitCliCommand>()));
root.Subcommands.Add(PortsCommand.Create(services.GetRequiredService<PortsCliCommand>()));
root.Subcommands.Add(MarketplaceCommands.Create(
    services.GetRequiredService<MarketplaceListCliCommand>(),
    services.GetRequiredService<MarketplaceSearchCliCommand>(),
    services.GetRequiredService<MarketplaceSubmitCliCommand>(),
    services.GetRequiredService<MarketplacePublishCliCommand>(),
    services.GetRequiredService<MarketplaceInfoCliCommand>(),
    services.GetRequiredService<MarketplaceInstallCliCommand>()));
root.Subcommands.Add(StorageCommands.Create(
    services.GetRequiredService<StorageShowCliCommand>(),
    services.GetRequiredService<StorageChangeCliCommand>()));
root.Subcommands.Add(DataCommands.Create(
    services.GetRequiredService<DataExportCliCommand>(),
    services.GetRequiredService<DataImportCliCommand>(),
    completions));
root.Subcommands.Add(AuditCommands.Create(services.GetRequiredService<AuditReplayCliCommand>()));
root.Subcommands.Add(AgentsCommand.Create(services.GetRequiredService<AgentsCliCommand>()));
root.Subcommands.Add(ToolsCommand.Create(services.GetRequiredService<ToolsCliCommand>()));
root.Subcommands.Add(TasksCommand.Create(services.GetRequiredService<TasksCliCommand>()));
root.Subcommands.Add(SystemCommand.Create(services.GetRequiredService<SystemCliCommand>()));
root.Subcommands.Add(VersionCommand.Create(services.GetRequiredService<VersionCliCommand>()));
root.Subcommands.Add(UpgradeCommand.Create(services.GetRequiredService<UpgradeCliCommand>()));

var config = new Command("config", "Manage CLI configuration");
config.Subcommands.Add(ConfigGetCommand.Create(services.GetRequiredService<ConfigGetCliCommand>()));
config.Subcommands.Add(ConfigSetCommand.Create(services.GetRequiredService<ConfigSetCliCommand>()));
root.Subcommands.Add(config);

return root.Parse(args).Invoke();
