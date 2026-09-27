using System.Net;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.SystemInfo;
using Weave.Actions.Workspace;
using Weave.Cli.Shell;
using Weave.Cli.Tui;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

public sealed class TuiWorkspaceStarterTests
{
    [Fact]
    public async Task DispatchAsync_CapabilityFile_SendsTokenOnWorkspaceStart()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var manifestPath = await WriteManifestAsync(directory);
            var capabilityPath = Path.Join(directory, "capability.txt");
            await File.WriteAllTextAsync(capabilityPath, "  encoded-token  ", TestContext.Current.CancellationToken);
            using var handler = new WorkspaceHandler();
            var (verb, session, client) = CreateStarter(manifestPath, handler);
            using (client)
            {
                await verb.DispatchAsync(new TuiVerbContext(session, capabilityPath, () => { }),
                    TestContext.Current.CancellationToken);

                handler.StartCalls.ShouldBe(1);
                handler.PresentedCapability.ShouldBe("encoded-token");
                session.WorkspaceId.ShouldBe("ws-1");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DispatchAsync_InvalidCapabilityFile_DoesNotStartWorkspace()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var manifestPath = await WriteManifestAsync(directory);
            var capabilityPath = Path.Join(directory, "capability.txt");
            await File.WriteAllTextAsync(capabilityPath, "invalid token with spaces",
                TestContext.Current.CancellationToken);
            using var handler = new WorkspaceHandler();
            var (verb, session, client) = CreateStarter(manifestPath, handler);
            using (client)
            {
                await verb.DispatchAsync(new TuiVerbContext(session, capabilityPath, () => { }),
                    TestContext.Current.CancellationToken);

                handler.StartCalls.ShouldBe(0);
                session.IsRunning.ShouldBeFalse();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DispatchAsync_NoCapabilityFile_StartsUnprotectedWorkspace()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var manifestPath = await WriteManifestAsync(directory);
            using var handler = new WorkspaceHandler(allowAnonymous: true);
            var (verb, session, client) = CreateStarter(manifestPath, handler);
            using (client)
            {
                await verb.DispatchAsync(new TuiVerbContext(session, null, () => { }),
                    TestContext.Current.CancellationToken);

                handler.StartCalls.ShouldBe(1);
                handler.PresentedCapability.ShouldBeNull();
                session.WorkspaceId.ShouldBe("ws-1");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (TuiWorkspaceStarter Verb, TuiSession Session, HttpClient Client) CreateStarter(
        string manifestPath, WorkspaceHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://example.test")
        };
        var session = new TuiSession(new FixedManifestResolver(manifestPath));
        session.TryOpen("demo", out _).ShouldBeTrue();
        session.AgentName = "already-selected";
        var agentSelector = new TuiAgentSelector(
            new TuiAgentNameSource(new ListAgentsAction(client)),
            new SelectAgentAction(Substitute.For<IActionPrompter>()));
        var verb = new TuiWorkspaceStarter(
            agentSelector, new StartWorkspaceAction(client), new GetSystemInfoAction(new TestConfigSource(), client),
            new UnexpectedSiloLauncher());
        return (verb, session, client);
    }

    private static string NewTemporaryDirectory()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-tui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> WriteManifestAsync(string directory)
    {
        var path = Path.Join(directory, "workspace.json");
        await File.WriteAllTextAsync(path, """{"version":"1.0","name":"demo"}""",
            TestContext.Current.CancellationToken);
        return path;
    }

    private sealed class FixedManifestResolver(string path) : IManifestResolver
    {
        public string? Resolve(string? workspace) => path;
    }

    private sealed class TestConfigSource : ISystemConfigSource
    {
        public SystemConfigSnapshot Load() => new()
        {
            Version = "test",
            BaseUrl = "https://example.test",
            DefaultPort = 0,
            Storage = "test",
            AuthMode = "none",
            RequireHttps = true,
            SiloPath = null,
            WeaveHome = "test"
        };
    }

    private sealed class UnexpectedSiloLauncher : ISiloLauncher
    {
        public string? ResolveSiloPath() => throw new InvalidOperationException("The silo is reachable.");

        public Task<bool> AutoStartServeAsync(CancellationToken ct) =>
            throw new InvalidOperationException("The silo is reachable.");

        public Task<SiloAutoStartResult> AutoStartServeWithDiagnosticsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("The silo is reachable.");
    }

    private sealed class WorkspaceHandler(bool allowAnonymous = false) : HttpMessageHandler
    {
        public int StartCalls { get; private set; }
        public string? PresentedCapability { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/health")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            StartCalls++;
            PresentedCapability = request.Headers.TryGetValues("X-Weave-Capability", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(PresentedCapability is null && !allowAnonymous
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.Created)
            {
                Content = new StringContent("""{"workspaceId":"ws-1","name":"demo","status":"Running","containerCount":0}""")
            });
        }
    }
}
