using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using Weave.Actions.Channel;
using Weave.Actions.Marketplace;
using Weave.Actions.Skill;
using Weave.Actions.SystemInfo;
using Weave.Actions.Template;
using Weave.Actions.Workspace;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

internal sealed class DataTransferFixture : HttpMessageHandler
{
    private readonly LocalTestDirectory _directory = new();
    public string Root => _directory.Root;
    public string ManifestPath => Path.Join(Root, "workspace.json");
    public string OutputPath => Path.Join(Root, "snapshot.json");
    public string Destination => Path.Join(Root, "restored");
    public RecordingWorkspaceRegistry Registry { get; } = new();
    public FixedManifestResolver Resolver { get; } = new(null);
    public HttpClient Client { get; }
    public List<(string Method, string Path, JsonNode? Body, string? Capability)> Requests { get; } = [];
    public Func<string, HttpResponseMessage> Respond { get; set; } =
        path => throw new InvalidOperationException("Unexpected request: " + path);

    public DataTransferFixture()
    {
        Client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://example.test") };
        Resolver.Path = ManifestPath;
    }

    public DataExportCliCommand ExportCommand() => new(Resolver, new WorkspacePrompt(Registry, Resolver),
        SystemInfo(), new ListSkillsAction(Client), new ListChannelsAction(Client),
        new ListTemplatesAction(Client), new ListMarketplaceItemsAction(Client));

    public DataImportCliCommand ImportCommand() => new(Registry, SystemInfo(), new StartWorkspaceAction(Client),
        new PostSkillAction(Client), new PostChannelAction(Client));

    private GetSystemInfoAction SystemInfo()
    {
        var config = Substitute.For<ISystemConfigSource>();
        config.Load().Returns(new SystemConfigSnapshot
        {
            Version = "1.0",
            BaseUrl = "https://example.test",
            DefaultPort = 9401,
            Storage = "memory",
            AuthMode = "none",
            RequireHttps = true,
            WeaveHome = Root,
            SiloPath = null
        });
        return new GetSystemInfoAction(config, Client);
    }

    public async Task WriteExportAsync(WorkspaceExport export) => await File.WriteAllTextAsync(OutputPath,
        JsonSerializer.Serialize(export, DataJsonContext.Default.WorkspaceExport), TestContext.Current.CancellationToken);

    public async Task<WorkspaceExport> ReadExportAsync() => JsonSerializer.Deserialize(
        await File.ReadAllTextAsync(OutputPath, TestContext.Current.CancellationToken),
        DataJsonContext.Default.WorkspaceExport).ShouldNotBeNull();

    public static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static HttpResponseMessage Response(int status, string json = "") => new((HttpStatusCode)status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
        var capability = request.Headers.TryGetValues("X-Weave-Capability", out var values) ? values.Single() : null;
        var path = request.RequestUri.ShouldNotBeNull().AbsolutePath;
        Requests.Add((request.Method.Method, path, body, capability));
        return Respond(path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Client.Dispose();
            _directory.Dispose();
        }
        base.Dispose(disposing);
    }
}
