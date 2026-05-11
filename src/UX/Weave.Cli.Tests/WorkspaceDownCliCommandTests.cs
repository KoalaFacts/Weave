using Weave.Actions.Context;
using Weave.Actions.Workspace;
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithWhitespaceWorkspaceId_ReturnsFailureWithoutReadingStateFile(string blankId)
    {
        var dependencies = new TestWorkspaceDownDependencies
        {
            ManifestPath = "workspace.json",
            StateExists = true,
            WorkspaceIdText = "from-state-not-read"
        };
        var command = new WorkspaceDownCliCommand(dependencies, Prompt);

        var result = await command.ExecuteAsync(
            new WorkspaceDownOptions("demo", "workspace.json", blankId),
            TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task ExecuteAsync_StopActionFails_ReturnsFailureAndLeavesStateFileIntact()
    {
        var dependencies = new TestWorkspaceDownDependencies
        {
            ManifestPath = "workspace.json",
            StateExists = true,
            WorkspaceIdText = "workspace-from-state",
            StopResult = ActionResult.Failed<StopWorkspaceResult>(
                ActionFailure.Conflict("Workspace is in a transitional state."))
        };
        var command = new WorkspaceDownCliCommand(dependencies, Prompt);

        var result = await command.ExecuteAsync(new WorkspaceDownOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        dependencies.StoppedWorkspaceId.ShouldBe("workspace-from-state");
        dependencies.DeletedPath.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_StopActionCancelled_Returns130AndLeavesStateFileIntact()
    {
        var dependencies = new TestWorkspaceDownDependencies
        {
            ManifestPath = "workspace.json",
            StateExists = true,
            WorkspaceIdText = "workspace-from-state",
            StopResult = ActionResult.Failed<StopWorkspaceResult>(ActionFailure.Cancelled())
        };
        var command = new WorkspaceDownCliCommand(dependencies, Prompt);

        var result = await command.ExecuteAsync(new WorkspaceDownOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(130);
        dependencies.StoppedWorkspaceId.ShouldBe("workspace-from-state");
        dependencies.DeletedPath.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_DeleteFileFails_ReturnsSuccessSinceSiloStopSucceeded()
    {
        var dependencies = new TestWorkspaceDownDependencies
        {
            ManifestPath = "workspace.json",
            StateExists = true,
            WorkspaceIdText = "workspace-from-state",
            DeleteFileException = new IOException("file is locked")
        };
        var command = new WorkspaceDownCliCommand(dependencies, Prompt);

        var result = await command.ExecuteAsync(new WorkspaceDownOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        dependencies.StoppedWorkspaceId.ShouldBe("workspace-from-state");
        dependencies.DeletedPath.ShouldBeNull();
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

        public ActionResult<StopWorkspaceResult> StopResult { get; init; } =
            ActionResult.Success(new StopWorkspaceResult());

        public Exception? DeleteFileException { get; init; }

        public int StopCalls { get; private set; }

        public string? StoppedWorkspaceId { get; private set; }

        public string? DeletedPath { get; private set; }

        public string? ResolveManifestPath(string? name) => ManifestPath;

        public string GetWorkspaceStatePath(string manifestPath) => "workspace.state";

        public bool FileExists(string path) => StateExists;

        public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => Task.FromResult(WorkspaceIdText);

        public Task<ActionResult<StopWorkspaceResult>> StopWorkspaceAsync(string workspaceId, CancellationToken ct)
        {
            StopCalls++;
            StoppedWorkspaceId = workspaceId;
            return Task.FromResult(StopResult);
        }

        public void DeleteFile(string path)
        {
            if (DeleteFileException is not null)
                throw DeleteFileException;
            DeletedPath = path;
        }
    }
}
