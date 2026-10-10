using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Weave.Actions.Agent;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class WorkspaceInspectionCommandTests
{
    private const string Manifest = """
        {"version":"1.0","name":"inspection-demo","agents":{"manifest-agent":{"model":"manifest-model","tools":["manifest-tool"]}},"tools":{"manifest-tool":{"type":"filesystem"}}}
        """;
    private const string LiveStatus = """
        {"workspaceId":"runtime-123","status":"Running","recoveryCondition":"RequiresReconciliation","containerCount":7}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UnresolvedManifest_ReturnsFailureWithoutHttp(bool validate)
    {
        using var fixture = new InspectionFixture(null);

        var result = validate
            ? await fixture.Validate().ExecuteAsync(new WorkspaceNameOptions("missing"), TestContext.Current.CancellationToken)
            : await fixture.Status().ExecuteAsync(new WorkspaceNameOptions("missing"), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Output.ShouldContain("No workspace.json found for 'missing'.");
        fixture.Handler.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NoState_RendersManifestWithoutHttp()
    {
        using var directory = new LocalTestDirectory();
        var path = await WriteManifestAsync(directory);
        using var fixture = new InspectionFixture(path);

        var result = await fixture.Status().ExecuteAsync(new WorkspaceNameOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Output.ShouldContain("inspection-demo");
        fixture.Output.ShouldContain("Agents (manifest)");
        fixture.Output.ShouldContain("manifest-agent");
        fixture.Output.ShouldContain("manifest-model");
        fixture.Output.ShouldContain("Tools (manifest)");
        fixture.Output.ShouldContain("manifest-tool");
        fixture.Output.ShouldContain("filesystem");
        fixture.Handler.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_LiveWorkspace_RendersRecoveryAndSortedEscapedRows()
    {
        using var directory = new LocalTestDirectory();
        var path = await WriteManifestAsync(directory, withState: true);
        using var fixture = new InspectionFixture(path);
        fixture.Handler.Respond = request => Json(request.RequestUri.ShouldNotBeNull().AbsolutePath switch
        {
            "/api/workspaces/runtime-123" => LiveStatus,
            "/api/workspaces/runtime-123/agents" => """
                [{"agentName":"z-agent","status":"Active","model":"z-model"},{"agentName":"a[agent]","status":"Idle","model":null}]
                """,
            "/api/workspaces/runtime-123/tools" => """
                [{"toolName":"z-tool","toolType":"cli","status":"Connected"},{"toolName":"a[tool]","toolType":"filesystem","status":"Disconnected"}]
                """,
            _ => throw new InvalidOperationException("Unexpected request")
        });

        var result = await fixture.Status().ExecuteAsync(new WorkspaceNameOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Handler.Paths.ShouldBe(["/api/workspaces/runtime-123", "/api/workspaces/runtime-123/agents", "/api/workspaces/runtime-123/tools"]);
        fixture.Output.ShouldContain("runtime-123");
        fixture.Output.ShouldContain("Containers");
        fixture.Output.ShouldContain("7");
        fixture.Output.ShouldContain("Needs review");
        fixture.Output.ShouldContain("Workspace recovery needs review.");
        fixture.Output.ShouldContain("a[agent]");
        fixture.Output.ShouldContain("a[tool]");
        fixture.Output.IndexOf("a[agent]", StringComparison.Ordinal).ShouldBeLessThan(fixture.Output.IndexOf("z-agent", StringComparison.Ordinal));
        fixture.Output.IndexOf("a[tool]", StringComparison.Ordinal).ShouldBeLessThan(fixture.Output.IndexOf("z-tool", StringComparison.Ordinal));
        fixture.Output.ShouldNotContain("Agents (manifest)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EmptyOrUnavailableLists_KeepsLiveStatusWithoutManifestFallback(bool unavailable)
    {
        using var directory = new LocalTestDirectory();
        var path = await WriteManifestAsync(directory, withState: true);
        using var fixture = new InspectionFixture(path);
        fixture.Handler.Respond = request => request.RequestUri.ShouldNotBeNull().AbsolutePath == "/api/workspaces/runtime-123"
            ? Json(LiveStatus.Replace("RequiresReconciliation", "StartedOnThisHost", StringComparison.Ordinal))
            : Json("[]", unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);

        var result = await fixture.Status().ExecuteAsync(new WorkspaceNameOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Output.ShouldContain("runtime-123");
        fixture.Output.ShouldContain("Running");
        fixture.Output.ShouldNotContain("Needs review");
        fixture.Output.ShouldNotContain("manifest-agent");
        fixture.Output.ShouldNotContain("manifest-tool");
        fixture.Handler.Paths.ShouldBe(["/api/workspaces/runtime-123", "/api/workspaces/runtime-123/agents", "/api/workspaces/runtime-123/tools"]);
    }

    [Fact]
    public async Task ExecuteAsync_UnreachableStatus_FallsBackWithoutFetchingLists()
    {
        using var directory = new LocalTestDirectory();
        var path = await WriteManifestAsync(directory, withState: true);
        using var fixture = new InspectionFixture(path);
        fixture.Handler.Respond = _ => throw new HttpRequestException("offline-fixture");

        var result = await fixture.Status().ExecuteAsync(new WorkspaceNameOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Handler.Paths.ShouldBe(["/api/workspaces/runtime-123"]);
        fixture.Output.ShouldContain("Falling back to manifest data.");
        fixture.Output.ShouldContain("manifest-agent");
        (await File.ReadAllTextAsync(WorkspaceManifestPaths.GetStatePath(path), TestContext.Current.CancellationToken)).ShouldBe("  runtime-123\n");
    }

    [Fact]
    public async Task ExecuteAsync_CancelledStatus_Returns130WithoutFallback()
    {
        using var directory = new LocalTestDirectory();
        var path = await WriteManifestAsync(directory, withState: true);
        using var fixture = new InspectionFixture(path);
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.Respond = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        var result = await fixture.Status().ExecuteAsync(new WorkspaceNameOptions("demo"), cancellation.Token);

        result.ShouldBe(130);
        fixture.Handler.Paths.Count.ShouldBe(1);
        fixture.Output.ShouldNotContain("manifest-agent");
        fixture.Output.ShouldNotContain("Falling back");
    }

    [Fact]
    public async Task ExecuteAsync_ValidManifest_PostsOriginalTextAndRendersCounts()
    {
        using var directory = new LocalTestDirectory();
        var path = await WriteManifestAsync(directory);
        using var fixture = new InspectionFixture(path);
        fixture.Handler.Respond = _ => Json("""{"name":"validated-demo","agentCount":2,"toolCount":3,"targetCount":4,"errors":[]}""");

        var result = await fixture.Validate().ExecuteAsync(new WorkspaceNameOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Handler.Paths.ShouldBe(["/api/workspaces/validate"]);
        fixture.Handler.Method.ShouldBe(HttpMethod.Post);
        using var posted = JsonDocument.Parse(fixture.Handler.Body.ShouldNotBeNull());
        posted.RootElement.GetProperty("manifestJson").GetString().ShouldBe(Manifest);
        fixture.Output.ShouldContain("Configuration valid.");
        fixture.Output.ShouldContain("validated-demo");
        fixture.Output.ShouldContain("Agents: 2");
        fixture.Output.ShouldContain("Tools: 3");
        fixture.Output.ShouldContain("Targets: 4");
    }

    [Theory]
    [InlineData(200, "{\"errors\":[\"Agent has no model\",\"Unknown tool reference\"]}", "Unknown tool reference")]
    [InlineData(400, "{\"errors\":{\"manifestJson\":[\"Invalid JSON fixture\"]}}", "Invalid JSON fixture")]
    [InlineData(403, "{}", "Silo refused the validate request (403).")]
    [InlineData(500, "{}", "Silo error validating manifest (500).")]
    public async Task ExecuteAsync_InvalidOrRejectedManifest_RendersFailure(int status, string body, string expected)
    {
        using var directory = new LocalTestDirectory();
        var path = await WriteManifestAsync(directory);
        using var fixture = new InspectionFixture(path);
        fixture.Handler.Respond = _ => Json(body, (HttpStatusCode)status);

        var result = await fixture.Validate().ExecuteAsync(new WorkspaceNameOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Output.ShouldContain(expected);
        fixture.Output.ShouldNotContain("Configuration valid.");
        if (status is 200 or 400)
            fixture.Output.ShouldContain("Configuration invalid:");
        else
            fixture.Output.ShouldNotContain("Configuration invalid");
        if (status == 200)
            fixture.Output.ShouldContain("Agent has no model");
    }

    [Fact]
    public async Task ExecuteAsync_ResolvedMissingFile_ReportsNotFoundWithoutHttp()
    {
        using var directory = new LocalTestDirectory();
        using var fixture = new InspectionFixture(Path.Join(directory.Root, "missing.json"));

        var result = await fixture.Validate().ExecuteAsync(new WorkspaceNameOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Output.ShouldContain("Manifest not found");
        fixture.Handler.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ValidationTransportFailure_ReportsUnreachable()
    {
        using var directory = new LocalTestDirectory();
        var path = await WriteManifestAsync(directory);
        using var fixture = new InspectionFixture(path);
        fixture.Handler.Respond = _ => throw new HttpRequestException("offline-fixture");

        var result = await fixture.Validate().ExecuteAsync(new WorkspaceNameOptions("demo"), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Output.ShouldContain("Silo unreachable");
        fixture.Output.ShouldNotContain("Configuration invalid");
        fixture.Output.ShouldNotContain("Configuration valid.");
    }

    [Fact]
    public async Task ExecuteAsync_CancelledValidation_Returns130WithoutSuccess()
    {
        using var directory = new LocalTestDirectory();
        var path = await WriteManifestAsync(directory);
        using var fixture = new InspectionFixture(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await fixture.Validate().ExecuteAsync(new WorkspaceNameOptions("demo"), cancellation.Token);

        result.ShouldBe(130);
        fixture.Handler.Paths.ShouldBeEmpty();
        fixture.Output.ShouldNotContain("Configuration valid.");
    }

    private static async Task<string> WriteManifestAsync(LocalTestDirectory directory, bool withState = false)
    {
        var path = Path.Join(directory.Root, "workspace.json");
        await File.WriteAllTextAsync(path, Manifest, TestContext.Current.CancellationToken);
        if (withState)
        {
            Directory.CreateDirectory(directory.Private);
            await File.WriteAllTextAsync(WorkspaceManifestPaths.GetStatePath(path), "  runtime-123\n", TestContext.Current.CancellationToken);
        }
        return path;
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class InspectionFixture : IDisposable
    {
        private readonly IAnsiConsole _original = AnsiConsole.Console;
        private readonly StringWriter _writer = new(CultureInfo.InvariantCulture);
        private readonly HttpClient _client;
        private readonly IManifestResolver _resolver;
        private readonly WorkspacePrompt _prompt;
        public InspectionHandler Handler { get; } = new();
        public string Output => _writer.ToString();

        public InspectionFixture(string? path)
        {
            _resolver = new InspectionManifestResolver(path);
            _prompt = new WorkspacePrompt(new InspectionRegistry(), _resolver);
            _client = new HttpClient(Handler) { BaseAddress = new Uri("https://inspection.test") };
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(_writer)
            });
            console.Profile.Width = 240;
            AnsiConsole.Console = console;
        }

        public WorkspaceStatusCliCommand Status() => new(new GetWorkspaceStatusAction(_client),
            new ListAgentsAction(_client), new ListToolsAction(_client), _resolver, _prompt);
        public WorkspaceValidateCliCommand Validate() => new(new ValidateWorkspaceAction(_client), _resolver, _prompt);

        public void Dispose()
        {
            AnsiConsole.Console = _original;
            _client.Dispose();
            _writer.Dispose();
        }
    }

    private sealed class InspectionManifestResolver(string? path) : IManifestResolver
    {
        public string? Resolve(string? workspace) => path;
    }

    private sealed class InspectionRegistry : IWorkspaceRegistry
    {
        public void Register(string name, string absolutePath) => throw new InvalidOperationException("Unexpected registry write");
        public void Unregister(string name) => throw new InvalidOperationException("Unexpected registry write");
        public string? Resolve(string name) => null;
        public IReadOnlyDictionary<string, string> GetAll() => new Dictionary<string, string>();
        public IEnumerable<string> GetNames() => [];
    }

    private sealed class InspectionHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => throw new InvalidOperationException("Unexpected HTTP request");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri.ShouldNotBeNull().AbsolutePath);
            Method = request.Method;
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return Respond(request);
        }
    }
}
