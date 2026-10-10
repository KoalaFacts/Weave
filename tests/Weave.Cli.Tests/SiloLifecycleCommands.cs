using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

internal static class SiloLifecycleCommands
{
    public static async Task VerifyServeAsync(string root, bool background)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        await using var worker = new SiloLifecycleWorker(Path.Join(root, "worker"), background ? "healthy" : "exit");
        worker.ReleasePort();
        var command = new ServeCliCommand(new FixtureLauncher(worker.Executable));
        var serving = command.ExecuteAsync(new ServeOptions(worker.Port, background), deadline.Token);
        try
        {
            await worker.ObserveStartedAsync(serving, deadline.Token);
            (await worker.ArgumentsAsync(deadline.Token)).ShouldBe([
                "--Weave:LocalMode=true", $"--urls=http://localhost:{worker.Port}"
            ]);
            if (!background)
                worker.AllowExit();

            (await serving.WaitAsync(deadline.Token)).ShouldBe(background ? 0 : 23);

            if (background)
            {
                (await worker.HealthObservedAsync(deadline.Token))["status"].ShouldNotBeNull().GetValue<int>().ShouldBe(200);
                worker.HasExited.ShouldBeFalse();
                (await SiloProcessService.IsReachableAsync(worker.Port, deadline.Token)).ShouldBeTrue();
                // The fixture owns cleanup of this intentionally backgrounded worker.
            }
            else
            {
                await worker.WaitForExitAsync(deadline.Token);
                worker.HasExited.ShouldBeTrue();
            }
        }
        finally
        {
            if (!serving.IsCompleted)
            {
                deadline.Cancel();
                try
                { await serving.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    // Independent fixture/group cleanup still owns any child on cancellation.
                }
            }
        }
    }

    public static async Task VerifyRunAsync(string root, bool rejected)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var worker = new SiloLifecycleWorker(Path.Join(root, "worker"), rejected ? "reject" : "healthy");
        var path = Path.Join(root, "workspace.json");
        await WorkspaceManifestFile.WriteAsync(path, new WorkspaceManifest
        {
            Name = "owned-run",
            Version = "1.0",
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["assistant"] = new()
                {
                    Model = "fixture-model",
                    SystemPromptFile = "./prompts/assistant.md",
                    Capabilities = ["tool:files:invoke:read"]
                }
            }
        }, deadline.Token);
        var original = await File.ReadAllTextAsync(path, deadline.Token);
        var state = WorkspaceManifestPaths.GetStatePath(path);
        if (rejected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(state).ShouldNotBeNull());
            await File.WriteAllTextAsync(state, "prior-id", deadline.Token);
        }
        var capability = Path.Join(root, "capability.txt");
        await File.WriteAllTextAsync(capability, "  fixture-capability\n", deadline.Token);
        var command = new RunCliCommand(new FixedManifestResolver(path), new RecordingWorkspaceRegistry(),
            new FixtureLauncher(worker.Executable), SiloLifecycleConfiguration.MemoryService());
        worker.ReleasePort();
        var running = command.ExecuteAsync(new RunOptions("owned-run", worker.Port, capability), cancellation.Token);
        try
        {
            await worker.ObserveStartedAsync(running, deadline.Token);
            var request = await worker.RequestAsync(deadline.Token);
            request["headers"].ShouldNotBeNull()["x-weave-capability"].ShouldNotBeNull().GetValue<string>()
                .ShouldBe("fixture-capability");
            var agent = request["body"].ShouldNotBeNull()["manifest"].ShouldNotBeNull()["agents"].ShouldNotBeNull()["assistant"].ShouldNotBeNull();
            agent["systemPromptFile"].ShouldNotBeNull().GetValue<string>().ShouldBe(Path.Join(root, "prompts", "assistant.md"));
            agent["capabilities"].ShouldNotBeNull().AsArray().Select(value => value.ShouldNotBeNull().GetValue<string>())
                .ShouldBe(["tool:files:invoke:read"]);
            if (!rejected)
            {
                while (!File.Exists(state) || await File.ReadAllTextAsync(state, deadline.Token) != "owned-run-id")
                {
                    if (running.IsCompleted)
                    {
                        await running;
                        throw new InvalidOperationException("Run finished before recording its successful startup.");
                    }
                    await Task.Delay(10, deadline.Token);
                }
                worker.HasExited.ShouldBeFalse();
                running.IsCompleted.ShouldBeFalse();
                cancellation.Cancel();
            }

            (await running.WaitAsync(deadline.Token)).ShouldBe(rejected ? 1 : 0);
            await worker.WaitForExitAsync(deadline.Token);
            worker.HasExited.ShouldBeTrue();
            (await File.ReadAllTextAsync(state, deadline.Token)).ShouldBe(rejected ? "prior-id" : "owned-run-id");
            (await File.ReadAllTextAsync(path, deadline.Token)).ShouldBe(original);
        }
        finally
        {
            cancellation.Cancel();
            if (!running.IsCompleted)
            {
                try
                { await running.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // Startup cancellation cleanup is not claimed by this success/rejection test.
                }
            }
        }
    }

    private sealed class FixtureLauncher(string executable) : ISiloLauncher
    {
        public string? ResolveSiloPath() => executable;
        public Task<bool> AutoStartServeAsync(CancellationToken ct) => throw new InvalidOperationException("Unexpected launcher delegation.");
        public Task<SiloAutoStartResult> AutoStartServeWithDiagnosticsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected launcher delegation.");
    }
}
