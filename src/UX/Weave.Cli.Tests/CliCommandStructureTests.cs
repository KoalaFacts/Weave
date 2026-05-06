using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Weave.Cli;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class CliCommandStructureTests
{
    [Fact]
    public void Commands_AllPositionalArguments_AreOptional()
    {
        var requiredArguments = AllCommands()
            .SelectMany(command => Flatten(command))
            .SelectMany(command => command.Arguments
                .Where(argument => argument.Arity.MinimumNumberOfValues > 0)
                .Select(argument => $"{command.Name} {argument.Name}: {argument.Arity.MinimumNumberOfValues}"))
            .ToArray();

        requiredArguments.ShouldBeEmpty();
    }

    private static IEnumerable<Command> AllCommands()
    {
        var services = CliServiceCollection.Build();
        var workspace = new Command("workspace", "Manage workspaces");
        workspace.Subcommands.Add(WorkspaceNewCommand.Create());
        workspace.Subcommands.Add(WorkspaceListCommand.Create());
        workspace.Subcommands.Add(WorkspaceRemoveCommand.Create());
        workspace.Subcommands.Add(WorkspaceUpCommand.Create());
        workspace.Subcommands.Add(WorkspaceDownCommand.Create());
        workspace.Subcommands.Add(WorkspaceStatusCommand.Create(services.GetRequiredService<WorkspaceStatusCliCommand>()));
        workspace.Subcommands.Add(WorkspaceShowCommand.Create());
        workspace.Subcommands.Add(WorkspaceValidateCommand.Create(services.GetRequiredService<WorkspaceValidateCliCommand>()));
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

        yield return workspace;
        yield return WorkspaceServeCommand.Create();
        yield return RunCommand.Create();
        yield return TuiCommand.Create(services.GetRequiredService<TuiCliCommand>());
        yield return WebUiCommand.Create();
        yield return InitCommand.Create();
        yield return PortsCommand.Create();
        yield return MarketplaceCommands.Create();
        yield return StorageCommands.Create();
        yield return DataCommands.Create();
        yield return VersionCommand.Create();
        yield return UpgradeCommand.Create(services.GetRequiredService<UpgradeCliCommand>());

        var config = new Command("config", "Manage CLI configuration");
        config.Subcommands.Add(ConfigGetCommand.Create());
        config.Subcommands.Add(ConfigSetCommand.Create());
        yield return config;
    }

    private static IEnumerable<Command> Flatten(Command command)
    {
        yield return command;

        foreach (var subcommand in command.Subcommands)
        {
            foreach (var child in Flatten(subcommand))
                yield return child;
        }
    }
}
