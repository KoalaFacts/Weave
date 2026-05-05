using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Weave.Cli;
using Weave.Cli.Commands;
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

var root = new RootCommand("weave — set up AI assistants with guardrails you control.");

var workspace = new Command("workspace", "Manage workspaces");
root.Subcommands.Add(workspace);

workspace.Subcommands.Add(WorkspaceNewCommand.Create());
workspace.Subcommands.Add(WorkspaceListCommand.Create());
workspace.Subcommands.Add(WorkspaceRemoveCommand.Create());
workspace.Subcommands.Add(WorkspaceUpCommand.Create());
workspace.Subcommands.Add(WorkspaceDownCommand.Create());
workspace.Subcommands.Add(WorkspaceStatusCommand.Create());
workspace.Subcommands.Add(WorkspaceShowCommand.Create());
workspace.Subcommands.Add(WorkspaceValidateCommand.Create());
workspace.Subcommands.Add(WorkspacePublishCommand.Create());
workspace.Subcommands.Add(WorkspacePresetsCommand.Create());

var add = new Command("add", "Add components to a workspace");
add.Subcommands.Add(WorkspaceAddAgentCommand.Create());
add.Subcommands.Add(WorkspaceAddToolCommand.Create());
add.Subcommands.Add(WorkspaceAddTargetCommand.Create());
add.Subcommands.Add(WorkspaceAddPluginCommand.Create());
workspace.Subcommands.Add(add);

workspace.Subcommands.Add(WorkspaceStorageCommands.Create());

var plugin = new Command("plugin", "Manage workspace plugins");
plugin.Subcommands.Add(WorkspacePluginListCommand.Create());
plugin.Subcommands.Add(WorkspaceAddPluginCommand.Create());
plugin.Subcommands.Add(WorkspacePluginRemoveCommand.Create());
workspace.Subcommands.Add(plugin);

root.Subcommands.Add(WorkspaceServeCommand.Create());
root.Subcommands.Add(RunCommand.Create());
root.Subcommands.Add(TuiCommand.Create(services.GetRequiredService<TuiCliCommand>()));
root.Subcommands.Add(WebUiCommand.Create());
root.Subcommands.Add(InitCommand.Create());
root.Subcommands.Add(PortsCommand.Create());
root.Subcommands.Add(MarketplaceCommands.Create());
root.Subcommands.Add(StorageCommands.Create());
root.Subcommands.Add(DataCommands.Create());
root.Subcommands.Add(AuditCommands.Create());
root.Subcommands.Add(SystemCommand.Create(services.GetRequiredService<SystemCliCommand>()));
root.Subcommands.Add(VersionCommand.Create());
root.Subcommands.Add(UpgradeCommand.Create(services.GetRequiredService<UpgradeCliCommand>()));

var config = new Command("config", "Manage CLI configuration");
config.Subcommands.Add(ConfigGetCommand.Create());
config.Subcommands.Add(ConfigSetCommand.Create());
root.Subcommands.Add(config);

return root.Parse(args).Invoke();
