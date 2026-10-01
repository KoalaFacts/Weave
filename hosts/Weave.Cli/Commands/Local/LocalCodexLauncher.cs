using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Weave.Cli.Commands.Local;

internal sealed class LocalCodexLauncher(ILocalDeploymentStore store, TimeProvider clock)
{
    public async Task<int> RunAsync(string directory, LocalDeployment deployment, string executable,
        string agentDirectory, string? task, bool execute, CancellationToken ct)
    {
        agentDirectory = Path.GetFullPath(agentDirectory);
        var pathError = LocalDeploymentStore.SeparateDirectoriesError(agentDirectory, directory)
            ?? LocalDeploymentStore.SeparateDirectoriesError(agentDirectory, deployment.DocumentsPath);
        if (pathError is not null)
        {
            Console.Error.WriteLine(pathError);
            return 1;
        }
        Directory.CreateDirectory(agentDirectory);
        using var client = LocalHttp.CreateClient(deployment.Origin);
        var capability = await new LocalHttp(client, clock).IssueAsync("agent", store.OperatorKey(directory), ct);
        var info = BuildStartInfo(executable, directory, deployment, agentDirectory, task, execute);
        info.Environment["WEAVE_AGENT_CAPABILITY"] = capability;
        using var process = Process.Start(info) ?? throw new IOException("Codex did not start. Install and sign in to the official Codex CLI, or supply --codex with its executable.");
        try
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, string directory, LocalDeployment deployment,
        string agentDirectory, string? task, bool execute)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = agentDirectory };
        foreach (var name in info.Environment.Keys.ToArray())
        {
            if (name.StartsWith("WEAVE", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CapabilityTokens", StringComparison.OrdinalIgnoreCase))
                info.Environment.Remove(name);
        }
        if (execute)
        {
            if (string.IsNullOrWhiteSpace(task))
                throw new ArgumentException("Provide --task for a noninteractive Codex run.");
            foreach (var argument in new[] { "exec", "--approve-for-me", "--skip-git-repo-check", "--json" })
                info.ArgumentList.Add(argument);
        }
        info.ArgumentList.Add("--ignore-user-config");
        info.ArgumentList.Add("--disable");
        info.ArgumentList.Add("plugins");
        info.ArgumentList.Add("--cd");
        info.ArgumentList.Add(agentDirectory);
        var command = Environment.ProcessPath ?? throw new IOException("CLI executable path is unavailable.");
        var bridge = new JsonArray();
        if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            bridge.Add((JsonNode?)JsonValue.Create(Path.Combine(AppContext.BaseDirectory, "weave.dll")));
        foreach (var argument in new[] { "local", "mcp", "--url", deployment.Origin, "--workspace", deployment.Workspace,
            "--receipts", Path.Combine(directory, "receipts") })
            bridge.Add((JsonNode?)JsonValue.Create(argument));
        var settings = new JsonObject
        {
            ["command"] = command, ["args"] = bridge,
            ["env_vars"] = new JsonArray("WEAVE_AGENT_CAPABILITY"), ["required"] = true,
            ["startup_timeout_sec"] = 10, ["tool_timeout_sec"] = 45,
            ["enabled_tools"] = new JsonArray("read_document", "submit_write", "get_status", "resume_write")
        };
        foreach (var setting in settings)
        {
            info.ArgumentList.Add("--config");
            info.ArgumentList.Add("mcp_servers.weave_files." + setting.Key + "=" + setting.Value!.ToJsonString());
        }
        info.ArgumentList.Add("Only use weave_files for governed documents. Choose and retain an invocation UUID before submitting a write. "
            + "Stop at Pending for a human to review with weave local review --id UUID in a separate terminal. "
            + "After the human decision, query the same UUID first; resume the server-retained original only if Approved with no recorded execution. "
            + "Never change content, create a replacement UUID to bypass a decision or unknown outcome, or seek operator/reviewer secrets. "
            + "This same-user local setup is not an OS sandbox. " + (task ?? "Wait for the user to give a document task."));
        return info;
    }
}
