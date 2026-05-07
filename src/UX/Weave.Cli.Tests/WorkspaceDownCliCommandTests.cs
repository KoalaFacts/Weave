using Weave.Cli.Commands;
using Weave.Cli.Shell;

namespace Weave.Cli.Tests;

public class WorkspaceDownCliCommandTests
{
    private static readonly WorkspacePrompt Prompt = new(new EmptyRegistry(), new NullManifestResolver());

    [Fact]
    public async Task ExecuteAsync_NoManifest_ReturnsFailure()
    {
        var dependencies = new TestWorkspaceDownDependencies { ManifestPath = null };
        var command = new WorkspaceDownCliCommand(dependencies, Prompt);

        var result = await command.ExecuteAsync(new WorkspaceDownOptions("missing"), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        dependencies.StopCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_NotRunning_ReturnsFailure()
    {
        var dependencies = new TestWorkspaceDownDependencies
        {
            ManifestPath = "workspace.json",
            StateExists = false
        };
        var command = new WorkspaceDownCliCommand(dependencies, Prompt);

        var result = await command.ExecuteAsync(new WorkspaceDownOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        dependencies.StopCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_StateFileEmpty_ReturnsFailure()
    {
        var dependencies = new TestWorkspaceDownDependencies
        {
            ManifestPath = "workspace.json",
            StateExists = true,
            WorkspaceIdText = "   "
        };
        var command = new WorkspaceDownCliCommand(dependencies, Prompt);

        var result = await command.ExecuteAsync(new WorkspaceDownOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        dependencies.StopCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkspaceId_StopsWorkspaceAndDeletesStateFile()
    {
        var dependencies = new TestWorkspaceDownDependencies
        {
            ManifestPath = "workspace.json",
            StateExists = true,
            WorkspaceIdText = "ignored"
        };
        var command = new WorkspaceDownCliCommand(dependencies, Prompt);

        var result = await command.ExecuteAsync(
            new WorkspaceDownOptions("demo", "workspace.json", "workspace-123"),
            TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        dependencies.StoppedWorkspaceId.ShouldBe("workspace-123");
        dependencies.DeletedPath.ShouldBe("workspace.state");
    }

    [Fact]
    public async Task ExecuteAsync_StateFileWorkspaceId_StopsWorkspaceAndDeletesStateFile()
    {
        var dependencies = new TestWorkspaceDownDependencies
        {
            ManifestPath = "workspace.json",
            StateExists = true,
            WorkspaceIdText = "workspace-from-state"
        };
        var command = new WorkspaceDownCliCommand(dependencies, Prompt);

        var result = await command.ExecuteAsync(new WorkspaceDownOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        dependencies.StoppedWorkspaceId.ShouldBe("workspace-from-state");
        dependencies.DeletedPath.ShouldBe("workspace.state");
    }

    private sealed class EmptyRegistry : IWorkspaceRegistry
    {
        public void Register(string name, string absolutePath) { }
        public void Unregister(string name) { }
        public string? Resolve(string name) => null;
        public IReadOnlyDictionary<string, string> GetAll() => new Dictionary<string, string>();
        public IEnumerable<string> GetNames() => [];
    }

    private sealed class NullManifestResolver : IManifestResolver
    {
        public string? Resolve(string? workspace) => null;
    }

    private sealed class TestWorkspaceDownDependencies : IWorkspaceDownDependencies
    {
        public string? ManifestPath { get; init; }

        public bool StateExists { get; init; }

        public string WorkspaceIdText { get; init; } = string.Empty;

        public int StopCalls { get; private set; }

        public string? StoppedWorkspaceId { get; private set; }

        public string? DeletedPath { get; private set; }

        public string? ResolveManifestPath(string? name) => ManifestPath;

        public string GetWorkspaceStatePath(string manifestPath) => "workspace.state";

        public bool FileExists(string path) => StateExists;

        public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => Task.FromResult(WorkspaceIdText);

        public Task StopWorkspaceAsync(string workspaceId, CancellationToken ct)
        {
            StopCalls++;
            StoppedWorkspaceId = workspaceId;
            return Task.CompletedTask;
        }

        public void DeleteFile(string path) => DeletedPath = path;
    }
}
