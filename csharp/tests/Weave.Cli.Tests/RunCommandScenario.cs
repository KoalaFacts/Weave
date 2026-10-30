using System.Text.Json.Nodes;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
using CliConfig = Weave.Cli.Shell.CliConfig;

namespace Weave.Cli.Tests;

internal static class RunCommandScenario
{
    public static async Task ExecuteAsync(string root, string mode)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var silo = new RunLoopbackSilo(root, mode);
        var port = await silo.ReadyAsync(deadline.Token);
        var manifestPath = Path.Join(root, "workspace.json");
        await WorkspaceManifestFile.WriteAsync(manifestPath, Manifest(), deadline.Token);
        var original = await File.ReadAllTextAsync(manifestPath, deadline.Token);
        var capabilityPath = Path.Join(root, "capability.txt");
        await File.WriteAllTextAsync(capabilityPath, mode == "empty-capability" ? " \n" : "  run-capability\n", deadline.Token);
        var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
        if (mode == "rejected")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath).ShouldNotBeNull());
            await File.WriteAllTextAsync(statePath, "previous-workspace-id", deadline.Token);
        }
        var launcher = new UnusedLauncher(mode == "missing-host");
        var command = new RunCliCommand(new FixedManifestResolver(manifestPath), new RecordingWorkspaceRegistry(), launcher,
            new SiloProcessService(new UnusedConfig(), new UnusedSecrets()));
        var running = command.ExecuteAsync(new RunOptions("run-workspace", port, capabilityPath), commandCancellation.Token);
        try
        {
            if (mode is "success" or "rejected" or "cancel-pending")
            {
                var request = await silo.StartObservedAsync(deadline.Token);
                AssertRequest(request, root);
                if (mode == "success")
                {
                    while (!File.Exists(statePath)
                        || await File.ReadAllTextAsync(statePath, deadline.Token) != "run-workspace-id")
                    {
                        if (running.IsCompleted)
                        {
                            await running;
                            throw new InvalidOperationException("Run returned before recording its workspace ID.");
                        }
                        await Task.Delay(10, deadline.Token);
                    }
                    commandCancellation.Cancel();
                }
                else if (mode == "cancel-pending")
                    commandCancellation.Cancel();
            }

            var result = await running.WaitAsync(deadline.Token);
            result.ShouldBe(mode is "rejected" or "empty-capability" or "missing-host" ? 1 : 0);
            if (mode == "success")
                (await File.ReadAllTextAsync(statePath, deadline.Token)).ShouldBe("run-workspace-id");
            else if (mode == "rejected")
                (await File.ReadAllTextAsync(statePath, deadline.Token)).ShouldBe("previous-workspace-id");
            else
                File.Exists(statePath).ShouldBeFalse();
            (await File.ReadAllTextAsync(manifestPath, deadline.Token)).ShouldBe(original);
            launcher.ResolveCalls.ShouldBe(mode == "missing-host" ? 1 : 0);
            string[] expectedRequests = mode switch
            {
                "empty-capability" => [],
                "missing-host" => ["GET /health"],
                _ => ["GET /health", "POST /api/workspaces"]
            };
            if (mode == "missing-host")
                await silo.HealthObservedAsync(deadline.Token);
            silo.Requests.ToArray().ShouldBe(expectedRequests);
            silo.HasExited.ShouldBeFalse();
            if (mode != "missing-host")
                (await SiloProcessService.IsReachableAsync(port, deadline.Token)).ShouldBeTrue();
        }
        finally
        {
            commandCancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static void AssertRequest(JsonObject request, string root)
    {
        request["method"].ShouldNotBeNull().GetValue<string>().ShouldBe("POST");
        request["headers"].ShouldNotBeNull()["x-weave-capability"].ShouldNotBeNull().GetValue<string>().ShouldBe("run-capability");
        var manifest = request["body"].ShouldNotBeNull()["manifest"].ShouldNotBeNull();
        manifest["name"].ShouldNotBeNull().GetValue<string>().ShouldBe("run-workspace");
        var agent = manifest["agents"].ShouldNotBeNull()["assistant"].ShouldNotBeNull();
        agent["systemPromptFile"].ShouldNotBeNull().GetValue<string>()
            .ShouldBe(Path.Join(root, "prompts", "assistant.md"));
        agent["capabilities"].ShouldNotBeNull().AsArray().Select(value => value.ShouldNotBeNull().GetValue<string>())
            .ShouldBe(["tool:documents:invoke:read"]);
    }

    private static WorkspaceManifest Manifest() => new()
    {
        Version = "1.0",
        Name = "run-workspace",
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["assistant"] = new()
            {
                Model = "model-marker",
                SystemPromptFile = "./prompts/assistant.md",
                Tools = ["documents"],
                Capabilities = ["tool:documents:invoke:read"]
            }
        },
        Tools = new Dictionary<string, ToolDefinition> { ["documents"] = new() { Type = "filesystem" } },
        Channels = new Dictionary<string, ChannelDefinition> { ["updates"] = new() { Type = "webhook", TargetAgent = "assistant" } }
    };

    private sealed class UnusedLauncher(bool allowMissingHost) : ISiloLauncher
    {
        public int ResolveCalls { get; private set; }
        public string? ResolveSiloPath()
        {
            ResolveCalls++;
            if (!allowMissingHost)
                throw new InvalidOperationException("Run must reuse the existing server.");
            return null;
        }
        public Task<bool> AutoStartServeAsync(CancellationToken ct) => throw new InvalidOperationException("Unexpected launch.");
        public Task<SiloAutoStartResult> AutoStartServeWithDiagnosticsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected launch.");
    }

    private sealed class UnusedConfig : IConfigStore
    {
        public CliConfig Load() => throw new InvalidOperationException("Run must not start another process.");
        public bool Exists() => throw new InvalidOperationException("Unexpected configuration lookup.");
        public void Save(CliConfig config) => throw new InvalidOperationException("Unexpected configuration write.");
    }

    private sealed class UnusedSecrets : ISecretResolver
    {
        public string? ResolveReference(string? reference) => throw new InvalidOperationException("Unexpected credential lookup.");
        public string ToEnvReference(string storageBackend) => throw new InvalidOperationException("Unexpected credential conversion.");
    }
}
