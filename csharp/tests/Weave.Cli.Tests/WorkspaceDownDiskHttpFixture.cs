using Weave.Actions.Workspace;

namespace Weave.Cli.Tests;

internal sealed class WorkspaceDownDiskHttpFixture : IDisposable
{
    public const string StateText = "  workspace-stop-42 \n";
    public const string FakeCapability = "fake-stop-capability";
    private const string CapabilityText = "  " + FakeCapability + "\n";
    private readonly HttpClient _client;
    private readonly FixedResolver _resolver;
    public string Root { get; } = Path.Join(Path.GetTempPath(), "weave-down-" + Guid.NewGuid().ToString("N"));
    public string StatePath { get; }
    public string ManifestPath { get; }
    public string CapabilityPath { get; }
    public StopHandler Handler { get; } = new();
    public WorkspaceDownCliCommand Command { get; }
    public WorkspaceDownOptions Options => new("owned-workspace", CapabilityFile: CapabilityPath);

    public WorkspaceDownDiskHttpFixture()
    {
        Directory.CreateDirectory(Path.Join(Root, ".weave"));
        StatePath = Path.Join(Root, ".weave", "workspace-id");
        ManifestPath = Path.Join(Root, "workspace.json");
        CapabilityPath = Path.Join(Root, "capability.txt");
        File.WriteAllText(StatePath, StateText);
        File.WriteAllText(ManifestPath, "{}");
        File.WriteAllText(CapabilityPath, CapabilityText);
        File.WriteAllText(Path.Join(Root, ".weave", "keep.txt"), "unrelated workspace data");
        _resolver = new FixedResolver(ManifestPath);
        _client = new HttpClient(Handler) { BaseAddress = new Uri("https://silo.test") };
        Command = new WorkspaceDownCliCommand(
            new DefaultWorkspaceDownDependencies(new StopWorkspaceAction(_client), _resolver),
            new WorkspacePrompt(new UnusedRegistry(), _resolver));
    }

    public void AssertStopRequest()
    {
        _resolver.RequestedName.ShouldBe("owned-workspace");
        Handler.Calls.ShouldBe(1);
        Handler.Method.ShouldBe(HttpMethod.Delete);
        Handler.RequestUri.ShouldBe(new Uri("https://silo.test/api/workspaces/workspace-stop-42"));
        Handler.Capability.ShouldBe(FakeCapability);
        Guid.TryParseExact(Handler.ManagementId, "N", out var operationId).ShouldBeTrue();
        operationId.ShouldNotBe(Guid.Empty);
    }

    public void AssertStateUnchanged()
    {
        File.ReadAllText(StatePath).ShouldBe(StateText);
        AssertOtherFilesUnchanged();
    }

    public void AssertOtherFilesUnchanged()
    {
        File.ReadAllText(ManifestPath).ShouldBe("{}");
        File.ReadAllText(CapabilityPath).ShouldBe(CapabilityText);
        File.ReadAllText(Path.Join(Root, ".weave", "keep.txt")).ShouldBe("unrelated workspace data");
    }

    public void Dispose()
    {
        _client.Dispose();
        Directory.Delete(Root, recursive: true);
    }

    internal sealed class StopHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Capability { get; private set; }
        public string? ManagementId { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => throw new InvalidOperationException("Unexpected HTTP stop request.");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Capability = request.Headers.GetValues("X-Weave-Capability").Single();
            ManagementId = request.Headers.GetValues("X-Weave-Management-Id").Single();
            return Respond(request, cancellationToken);
        }
    }

    private sealed class FixedResolver(string path) : IManifestResolver
    {
        public string? RequestedName { get; private set; }
        public string? Resolve(string? workspace)
        {
            RequestedName = workspace;
            return path;
        }
    }

    private sealed class UnusedRegistry : IWorkspaceRegistry
    {
        public void Register(string name, string absolutePath) => throw new InvalidOperationException("Unexpected registry mutation.");
        public void Unregister(string name) => throw new InvalidOperationException("Unexpected registry mutation.");
        public string? Resolve(string name) => throw new InvalidOperationException("Explicit name should bypass prompting.");
        public IReadOnlyDictionary<string, string> GetAll() => throw new InvalidOperationException("Explicit name should bypass prompting.");
        public IEnumerable<string> GetNames() => throw new InvalidOperationException("Explicit name should bypass prompting.");
    }
}
