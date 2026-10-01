using System.CommandLine;

namespace Weave.Cli.Commands.Local;

internal static class LocalCommands
{
    public static Command Create(LocalCliCommand handler)
    {
        var root = new Command("local", "Connect an existing Codex to local documents with human-controlled writes");
        var directory = new Option<string>("--directory") { DefaultValueFactory = _ => ".weave", Recursive = true, Description = "Private local deployment directory" };
        root.Options.Add(directory);

        var documents = new Option<string>("--documents") { Required = true, Description = "Existing document directory, outside the private deployment" };
        var host = new Option<string?>("--host") { Description = "Published Host executable (defaults to bundled host/)" };
        var workspace = new Option<string>("--workspace") { DefaultValueFactory = _ => "onboarding" };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 9401 };
        var init = new Command("init", "Initialize one protected local document approval Host") { documents, host, workspace, port };
        init.SetAction((parsed, ct) => LocalCliCommand.GuardAsync(() => handler.InitializeAsync(parsed.GetValue(directory)!,
            parsed.GetValue(documents)!, parsed.GetValue(host), parsed.GetValue(workspace)!, parsed.GetValue(port), ct)));
        root.Subcommands.Add(init);

        var serve = new Command("serve", "Run the published Host and reconnect the governed document tool");
        serve.SetAction((parsed, ct) => LocalCliCommand.GuardAsync(() => handler.ServeAsync(parsed.GetValue(directory)!, ct)));
        root.Subcommands.Add(serve);

        var executable = new Option<string>("--codex") { DefaultValueFactory = _ => "codex", Description = "Official Codex executable" };
        var agent = new Option<string>("--agent-directory") { DefaultValueFactory = _ => "agent", Description = "Agent working directory, separate from documents and private configuration" };
        var task = new Option<string?>("--task") { Description = "Document task to give Codex" };
        var execute = new Option<bool>("--exec") { Description = "Run a task noninteractively with Codex's normal automatic tool review" };
        var codex = new Command("codex", "Launch a fresh Codex with four Weave business tools and a narrow Agent credential") { executable, agent, task, execute };
        codex.SetAction((parsed, ct) => LocalCliCommand.GuardAsync(() => handler.CodexAsync(parsed.GetValue(directory)!,
            parsed.GetValue(executable)!, parsed.GetValue(agent)!, parsed.GetValue(task), parsed.GetValue(execute), ct)));
        root.Subcommands.Add(codex);

        foreach (var name in new[] { "review", "status" })
        {
            var id = new Option<string>("--id") { Required = true, Description = "Original invocation UUID" };
            var command = new Command(name, name == "review" ? "Personally review a Pending UUID; approval does not execute it" : "Query the original UUID without writing") { id };
            command.SetAction((parsed, ct) => LocalCliCommand.GuardAsync(() => name == "review"
                ? handler.ReviewAsync(parsed.GetValue(directory)!, parsed.GetValue(id)!, ct)
                : handler.StatusAsync(parsed.GetValue(directory)!, parsed.GetValue(id)!, ct)));
            root.Subcommands.Add(command);
        }

        var url = new Option<string>("--url") { Required = true };
        var mcpWorkspace = new Option<string>("--workspace") { Required = true };
        var receipts = new Option<string>("--receipts") { Required = true };
        var mcp = new Command("mcp", "Agent-only MCP stdio endpoint, configured by local codex") { url, mcpWorkspace, receipts };
        mcp.SetAction((parsed, ct) => LocalCliCommand.GuardAsync(() => handler.McpAsync(parsed.GetValue(url)!,
            parsed.GetValue(mcpWorkspace)!, parsed.GetValue(receipts)!, ct)));
        root.Subcommands.Add(mcp);
        return root;
    }
}
