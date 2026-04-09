using System.CommandLine;
using Weave.Cli.Commands;

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

var plugin = new Command("plugin", "Manage workspace plugins");
plugin.Subcommands.Add(WorkspacePluginListCommand.Create());
plugin.Subcommands.Add(WorkspaceAddPluginCommand.Create());
plugin.Subcommands.Add(WorkspacePluginRemoveCommand.Create());
workspace.Subcommands.Add(plugin);

root.Subcommands.Add(WorkspaceServeCommand.Create());
root.Subcommands.Add(InitCommand.Create());

var config = new Command("config", "Manage CLI configuration");
config.Subcommands.Add(ConfigGetCommand.Create());
config.Subcommands.Add(ConfigSetCommand.Create());
root.Subcommands.Add(config);

return root.Parse(args).Invoke();
