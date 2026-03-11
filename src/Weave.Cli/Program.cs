using Spectre.Console.Cli;
using Weave.Cli.Commands;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("weave");
    config.SetApplicationVersion("0.1.0");

    config.AddBranch("workspace", ws =>
    {
        ws.SetDescription("Manage workspaces");
        ws.AddCommand<WorkspaceNewCommand>("new").WithDescription("Create a new workspace");
        ws.AddCommand<WorkspaceListCommand>("list").WithDescription("List all workspaces");
        ws.AddCommand<WorkspaceRemoveCommand>("remove").WithDescription("Remove a workspace");
    });

    config.AddCommand<UpCommand>("up").WithDescription("Start a workspace");
    config.AddCommand<DownCommand>("down").WithDescription("Stop a workspace");
    config.AddCommand<StatusCommand>("status").WithDescription("Show workspace status");
    config.AddCommand<PublishCommand>("publish").WithDescription("Generate deployment manifests");

    config.AddBranch("config", cfg =>
    {
        cfg.SetDescription("Configuration management");
        cfg.AddCommand<ConfigShowCommand>("show").WithDescription("Show workspace configuration");
        cfg.AddCommand<ConfigValidateCommand>("validate").WithDescription("Validate workspace configuration");
    });
});

return app.Run(args);
