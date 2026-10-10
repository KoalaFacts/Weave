using System.Globalization;
using System.Net;
using Spectre.Console;
using Weave.Actions.SystemInfo;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

internal sealed class TuiViewTestContext : IDisposable
{
    public const string ManifestJson = """
        {"version":"1.0","workspace":{"isolation":"full"},"name":"review-space","agents":{"reviewer":{"model":"review-model","tools":["files"]}},"tools":{"files":{"type":"filesystem"}}}
        """;

    private readonly IAnsiConsole _previousConsole;
    private readonly ViewHandler _handler;
    private readonly StringWriter _writer = new(CultureInfo.InvariantCulture);
    private readonly string _directory = Path.Join(Path.GetTempPath(), $"weave-tui-views-{Guid.NewGuid():N}");

    public TuiViewTestContext()
    {
        Directory.CreateDirectory(_directory);
        ManifestPath = WriteManifest("workspace", ManifestJson);
        Session = new TuiSession(new FixedManifestResolver(ManifestPath));
        _handler = new ViewHandler(this);
        Client = new HttpClient(_handler) { BaseAddress = new Uri("https://silo.test") };
        _previousConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_writer),
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No
        });
        AnsiConsole.Console.Profile.Width = 240;
    }

    public string ManifestPath { get; }
    public TuiSession Session { get; }
    public HttpClient Client { get; }
    public List<string> Requests { get; } = [];
    public Func<string, HttpResponseMessage> Respond { get; set; } = _ => Json("[]");
    public string Output => _writer.ToString();
    public TuiVerbContext VerbContext => new(Session, null, () => { });

    public void Open(bool running = false, string? agent = null)
    {
        Session.TryOpen("review-space", out var error).ShouldBeTrue(error);
        if (running)
            Session.MarkRunning("workspace-42");
        Session.AgentName = agent;
    }

    public string WriteManifest(string name, string json)
    {
        var directory = Path.Join(_directory, name);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "workspace.json");
        File.WriteAllText(path, json);
        return path;
    }

    public static void WriteState(string manifestPath, string workspaceId = "workspace-42")
    {
        var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath).ShouldNotBeNull());
        File.WriteAllText(statePath, workspaceId);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public void Dispose()
    {
        AnsiConsole.Console = _previousConsole;
        Client.Dispose();
        _writer.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class FixedManifestResolver(string path) : IManifestResolver
    {
        public string? Resolve(string? workspace) => path;
    }

    private sealed class ViewHandler(TuiViewTestContext context) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri.ShouldNotBeNull().AbsolutePath;
            context.Requests.Add(path);
            return Task.FromResult(context.Respond(path));
        }
    }

    internal sealed class ConfigSource : ISystemConfigSource
    {
        public SystemConfigSnapshot Load() => new()
        {
            Version = "test",
            BaseUrl = "https://silo.test",
            DefaultPort = 9400,
            Storage = "test",
            AuthMode = "none",
            RequireHttps = true,
            SiloPath = null,
            WeaveHome = "unused-test-home"
        };
    }

    internal sealed class ReadOnlyRegistry(IReadOnlyDictionary<string, string> entries) : IWorkspaceRegistry
    {
        public IReadOnlyDictionary<string, string> GetAll() => entries;
        public IEnumerable<string> GetNames() => entries.Keys;
        public string? Resolve(string name) => entries.GetValueOrDefault(name);
        public void Register(string name, string absolutePath) => throw new InvalidOperationException("Registry writes are forbidden in view tests.");
        public void Unregister(string name) => throw new InvalidOperationException("Registry writes are forbidden in view tests.");
    }
}
