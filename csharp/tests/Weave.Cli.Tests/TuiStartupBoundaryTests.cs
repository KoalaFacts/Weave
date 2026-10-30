using System.Globalization;
using System.Net;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.SystemInfo;
using Weave.Actions.Workspace;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

[Collection("Tui view console")]
public sealed class TuiStartupBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAsync_NoWorkspaceOrAlreadyRunning_DoesNotLaunchOrCallSilo(bool running)
    {
        using var context = new TuiViewTestContext();
        if (running)
            context.Open(running: true, agent: "reviewer");
        var launcher = new ControlledLauncher();

        await Starter(context, launcher).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain(running ? "already running" : "No workspace open");
        context.Requests.ShouldBeEmpty();
        launcher.ResolveCalls.ShouldBe(0);
        launcher.LaunchCalls.ShouldBe(0);
    }

    [Fact]
    public async Task DispatchAsync_UnreachableSiloWithoutPath_ExplainsConfigurationWithoutStartingWorkspace()
    {
        using var context = new TuiViewTestContext();
        context.Open(agent: "reviewer");
        context.Respond = _ => TuiViewTestContext.Json("{}", HttpStatusCode.ServiceUnavailable);
        var launcher = new ControlledLauncher { Path = null };

        await Starter(context, launcher).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("Could not locate the Weave Silo on disk.");
        context.Output.ShouldContain("WEAVE_SILO_PATH");
        context.Requests.ShouldBe(["/health"]);
        launcher.ResolveCalls.ShouldBe(1);
        launcher.LaunchCalls.ShouldBe(0);
        context.Session.IsRunning.ShouldBeFalse();
        File.Exists(WorkspaceManifestPaths.GetStatePath(context.ManifestPath)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(18)]
    public async Task DispatchAsync_LaunchFailure_ShowsDiagnosticAndBoundedLiteralLogTail(int lineCount)
    {
        using var context = new TuiViewTestContext();
        context.Open(agent: "reviewer");
        context.Respond = _ => TuiViewTestContext.Json("{}", HttpStatusCode.ServiceUnavailable);
        var logPath = Path.Join(Path.GetDirectoryName(context.ManifestPath).ShouldNotBeNull(), "silo.log");
        if (lineCount > 0)
            await File.WriteAllLinesAsync(logPath,
                Enumerable.Range(1, lineCount).Select(index => "log[fixture]-" + index.ToString("D2", CultureInfo.InvariantCulture) + " -- controlled startup diagnostic with literal [details]"),
                TestContext.Current.CancellationToken);
        var launcher = new ControlledLauncher { Outcome = new SiloAutoStartResult(false, logPath, "launch-failed-fixture") };

        await Starter(context, launcher).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("Silo start failed: launch-failed-fixture");
        context.Output.ShouldContain(logPath);
        context.Requests.ShouldBe(["/health"]);
        launcher.LaunchCalls.ShouldBe(1);
        context.Session.IsRunning.ShouldBeFalse();
        context.Session.AgentName.ShouldBe("reviewer");
        File.Exists(WorkspaceManifestPaths.GetStatePath(context.ManifestPath)).ShouldBeFalse();
        if (lineCount == 0)
            context.Output.ShouldNotContain("lines of silo.log");
        else
        {
            context.Output.ShouldContain("last " + Math.Min(15, lineCount).ToString(CultureInfo.InvariantCulture) + " lines of silo.log");
            context.Output.ShouldContain("log[fixture]-" + lineCount.ToString("D2", CultureInfo.InvariantCulture));
            if (lineCount > 15)
            {
                context.Output.ShouldNotContain("log[fixture]-03");
                context.Output.ShouldContain("log[fixture]-04");
            }
        }
    }

    [Fact]
    public async Task DispatchAsync_LaunchFailureWithLockedLog_ExplainsUnavailableDiagnostics()
    {
        using var context = new TuiViewTestContext();
        context.Open();
        context.Respond = _ => TuiViewTestContext.Json("{}", HttpStatusCode.ServiceUnavailable);
        var logPath = Path.Join(Path.GetDirectoryName(context.ManifestPath).ShouldNotBeNull(), "silo.log");
        await File.WriteAllTextAsync(logPath, "locked-log-fixture", TestContext.Current.CancellationToken);
        using var locked = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var launcher = new ControlledLauncher { Outcome = new SiloAutoStartResult(false, logPath, null) };

        await Starter(context, launcher).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Output.ShouldContain("Silo start failed: unknown");
        context.Output.ShouldContain("could not read log:");
        context.Output.ShouldNotContain("locked-log-fixture");
        context.Session.IsRunning.ShouldBeFalse();
        context.Requests.ShouldBe(["/health"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAsync_LaunchedButStartFails_PreservesDiskStateSessionAndHistory(bool cancelled)
    {
        using var context = new TuiViewTestContext();
        context.Open(agent: "reviewer");
        TuiViewTestContext.WriteState(context.ManifestPath, "preserved-state");
        using var cancellation = new CancellationTokenSource();
        context.Respond = path =>
        {
            if (path == "/health")
                return TuiViewTestContext.Json("{}", HttpStatusCode.ServiceUnavailable);
            path.ShouldBe("/api/workspaces");
            if (cancelled)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return TuiViewTestContext.Json("{\"detail\":\"start-rejected-fixture\"}", HttpStatusCode.Conflict);
        };
        var launcher = new ControlledLauncher();
        List<string> history = ["retained-conversation"];

        await Starter(context, launcher).DispatchAsync(new TuiVerbContext(context.Session, null, history.Clear), cancellation.Token);

        context.Output.ShouldContain(cancelled ? "Start request cancelled." : "start-rejected-fixture");
        context.Output.ShouldContain("Failed to start:");
        context.Requests.ShouldBe(["/health", "/api/workspaces"]);
        launcher.LaunchCalls.ShouldBe(1);
        context.Session.IsRunning.ShouldBeFalse();
        context.Session.AgentName.ShouldBe("reviewer");
        history.ShouldBe(["retained-conversation"]);
        (await File.ReadAllTextAsync(WorkspaceManifestPaths.GetStatePath(context.ManifestPath), TestContext.Current.CancellationToken)).ShouldBe("preserved-state");
    }

    [Fact]
    public async Task DispatchAsync_LaunchAndStartSucceed_PersistsIdentityAndSelectsSoleAgent()
    {
        using var context = new TuiViewTestContext();
        context.Open();
        context.Respond = path => path == "/health"
            ? TuiViewTestContext.Json("{}", HttpStatusCode.ServiceUnavailable)
            : TuiViewTestContext.Json("""{"workspaceId":"new-runtime","status":"Running","recoveryCondition":"StartedOnThisHost","containerCount":0}""", HttpStatusCode.Created);
        var launcher = new ControlledLauncher();

        await Starter(context, launcher).DispatchAsync(context.VerbContext, TestContext.Current.CancellationToken);

        context.Requests.ShouldBe(["/health", "/api/workspaces"]);
        launcher.LaunchCalls.ShouldBe(1);
        context.Session.WorkspaceId.ShouldBe("new-runtime");
        context.Session.AgentName.ShouldBe("reviewer");
        (await File.ReadAllTextAsync(WorkspaceManifestPaths.GetStatePath(context.ManifestPath), TestContext.Current.CancellationToken)).ShouldBe("new-runtime");
        context.Output.ShouldContain("Workspace 'review-space' started.");
        context.Output.ShouldContain("Workspace ID: new-runtime");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAsync_UnreadableInput_DoesNotProbeOrLaunch(bool capability)
    {
        using var context = new TuiViewTestContext();
        context.Open();
        if (!capability)
            await File.WriteAllTextAsync(context.ManifestPath, "{ invalid", TestContext.Current.CancellationToken);
        var launcher = new ControlledLauncher();
        var args = capability ? Path.Join(Path.GetDirectoryName(context.ManifestPath).ShouldNotBeNull(), "missing-capability") : null;

        await Starter(context, launcher).DispatchAsync(new TuiVerbContext(context.Session, args, () => { }), TestContext.Current.CancellationToken);

        context.Output.ShouldContain(capability ? "Could not read capability file:" : "Failed to read manifest:");
        context.Requests.ShouldBeEmpty();
        launcher.ResolveCalls.ShouldBe(0);
        launcher.LaunchCalls.ShouldBe(0);
        context.Session.IsRunning.ShouldBeFalse();
    }

    private static TuiWorkspaceStarter Starter(TuiViewTestContext context, ISiloLauncher launcher) => new(
        new TuiAgentSelector(new TuiAgentNameSource(new ListAgentsAction(context.Client)), new SelectAgentAction(Substitute.For<IActionPrompter>())),
        new StartWorkspaceAction(context.Client), new GetSystemInfoAction(new TuiViewTestContext.ConfigSource(), context.Client), launcher);

    private sealed class ControlledLauncher : ISiloLauncher
    {
        public string? Path { get; init; } = "fixture-host";
        public SiloAutoStartResult Outcome { get; init; } = new(true, "unused-log", null);
        public int ResolveCalls { get; private set; }
        public int LaunchCalls { get; private set; }
        public string? ResolveSiloPath()
        {
            ResolveCalls++;
            return Path;
        }
        public Task<bool> AutoStartServeAsync(CancellationToken ct) => throw new InvalidOperationException("Expected diagnostic launcher path");
        public Task<SiloAutoStartResult> AutoStartServeWithDiagnosticsAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LaunchCalls++;
            return Task.FromResult(Outcome);
        }
    }
}
