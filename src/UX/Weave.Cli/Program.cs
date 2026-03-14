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

        ws.AddCommand<WorkspaceUpCommand>("up").WithDescription("Start a workspace");
        ws.AddCommand<WorkspaceDownCommand>("down").WithDescription("Stop a workspace");
        ws.AddCommand<WorkspaceStatusCommand>("status").WithDescription("Show workspace status");

        ws.AddCommand<WorkspaceShowCommand>("show").WithDescription("Show workspace configuration");
        ws.AddCommand<WorkspaceValidateCommand>("validate").WithDescription("Validate workspace configuration");
        ws.AddCommand<WorkspacePublishCommand>("publish").WithDescription("Generate deployment manifests");
        ws.AddCommand<WorkspacePresetsCommand>("presets").WithDescription("Browse ready-made workspace templates");

        ws.AddBranch("add", add =>
        {
            add.SetDescription("Add components to a workspace");
            add.AddCommand<WorkspaceAddAgentCommand>("agent").WithDescription("Add an assistant");
            add.AddCommand<WorkspaceAddToolCommand>("tool").WithDescription("Add a tool");
            add.AddCommand<WorkspaceAddTargetCommand>("target").WithDescription("Add a deployment target");
        });
    });

    config.AddCommand<WorkspaceServeCommand>("serve")
        .WithDescription("Start the local Weave server");
});

return app.Run(args);
