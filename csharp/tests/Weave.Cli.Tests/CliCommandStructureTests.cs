using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Weave.Cli;
using Weave.Cli.Commands;
using Weave.Cli.Shell;

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
        var configStore = services.GetRequiredService<IConfigStore>();
        var completions = services.GetRequiredService<WorkspaceCompletions>();

        var workspace = new Command("workspace", "Manage workspaces");
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

        yield return workspace;
        yield return WorkspaceServeCommand.Create(services.GetRequiredService<ServeCliCommand>(), configStore);
        yield return RunCommand.Create(services.GetRequiredService<RunCliCommand>(), configStore);
        yield return TuiCommand.Create(services.GetRequiredService<TuiCliCommand>());
        yield return WebUiCommand.Create(services.GetRequiredService<WebUiCliCommand>());
        yield return InitCommand.Create(services.GetRequiredService<InitCliCommand>());
        yield return PortsCommand.Create(services.GetRequiredService<PortsCliCommand>());
        yield return MarketplaceCommands.Create(
            services.GetRequiredService<MarketplaceListCliCommand>(),
            services.GetRequiredService<MarketplaceSearchCliCommand>(),
            services.GetRequiredService<MarketplaceSubmitCliCommand>(),
            services.GetRequiredService<MarketplacePublishCliCommand>(),
            services.GetRequiredService<MarketplaceInfoCliCommand>(),
            services.GetRequiredService<MarketplaceInstallCliCommand>());
        yield return StorageCommands.Create(
            services.GetRequiredService<StorageShowCliCommand>(),
            services.GetRequiredService<StorageChangeCliCommand>());
        yield return DataCommands.Create(
            services.GetRequiredService<DataExportCliCommand>(),
            services.GetRequiredService<DataImportCliCommand>(),
            completions);
        yield return VersionCommand.Create(services.GetRequiredService<VersionCliCommand>());
        yield return UpgradeCommand.Create(services.GetRequiredService<UpgradeCliCommand>());

        var config = new Command("config", "Manage CLI configuration");
        config.Subcommands.Add(ConfigGetCommand.Create(services.GetRequiredService<ConfigGetCliCommand>()));
        config.Subcommands.Add(ConfigSetCommand.Create(services.GetRequiredService<ConfigSetCliCommand>()));
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
