using System.Net;
using System.Text.Json;
using Weave.Actions.Channel;
using Weave.Actions.Skill;
using Weave.Actions.SystemInfo;
using Weave.Actions.Workspace;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class DataImportCliCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ReachableServerRejectsStartup_ReturnsFailureAndRetainsFiles()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var exportPath = Path.Join(directory, "export.json");
            var capabilityPath = Path.Join(directory, "capability.txt");
            var workspacePath = Path.Join(directory, "restored");
            var export = new WorkspaceExport
            {
                WorkspaceName = "demo",
                Manifest = """{"version":"1.0","name":"demo"}"""
            };
            await File.WriteAllTextAsync(exportPath,
                JsonSerializer.Serialize(export, DataJsonContext.Default.WorkspaceExport),
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(capabilityPath, "  encoded-token  ",
                TestContext.Current.CancellationToken);
            using var handler = new ImportHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var command = new DataImportCliCommand(
                new TestWorkspaceRegistry(),
                new GetSystemInfoAction(new TestConfigSource(), client),
                new StartWorkspaceAction(client), new PostSkillAction(client), new PostChannelAction(client));

            var result = await command.ExecuteAsync(
                new DataImportOptions(exportPath, workspacePath, capabilityPath),
                TestContext.Current.CancellationToken);

            result.ShouldBe(1);
            handler.PresentedCapability.ShouldBe("encoded-token");
            File.Exists(Path.Join(workspacePath, "workspace.json")).ShouldBeTrue();
            File.Exists(Path.Join(workspacePath, ".weave", "workspace-id")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SkillRestoreFails_ReturnsFailureAfterWorkspaceStarted()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var exportPath = Path.Join(directory, "export.json");
            var workspacePath = Path.Join(directory, "restored");
            using var skillDocument = JsonDocument.Parse("{}");
            var export = new WorkspaceExport
            {
                WorkspaceName = "demo",
                Manifest = """{"version":"1.0","name":"demo"}""",
                Skills = [skillDocument.RootElement.Clone()]
            };
            await File.WriteAllTextAsync(exportPath,
                JsonSerializer.Serialize(export, DataJsonContext.Default.WorkspaceExport),
                TestContext.Current.CancellationToken);
            using var handler = new ImportHandler(startSucceeds: true);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var command = new DataImportCliCommand(
                new TestWorkspaceRegistry(),
                new GetSystemInfoAction(new TestConfigSource(), client),
                new StartWorkspaceAction(client), new PostSkillAction(client), new PostChannelAction(client));

            var result = await command.ExecuteAsync(
                new DataImportOptions(exportPath, workspacePath),
                TestContext.Current.CancellationToken);

            result.ShouldBe(1);
            handler.SkillPostCount.ShouldBe(1);
            File.Exists(Path.Join(workspacePath, ".weave", "workspace-id")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ImportHandler(bool startSucceeds = false) : HttpMessageHandler
    {
        private readonly HttpResponseMessage _health = new(HttpStatusCode.OK);
        private readonly HttpResponseMessage _denied = new(HttpStatusCode.Unauthorized);
        private readonly HttpResponseMessage _started = new(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"workspaceId":"ws-1","name":"demo","status":"Running","containerCount":0}""")
        };
        private readonly HttpResponseMessage _skillDenied = new(HttpStatusCode.Forbidden);

        public string? PresentedCapability { get; private set; }

        public int SkillPostCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/health")
                return Task.FromResult(_health);

            if (request.RequestUri?.AbsolutePath == "/api/workspaces" && startSucceeds)
                return Task.FromResult(_started);

            if (request.RequestUri?.AbsolutePath.EndsWith("/skills", StringComparison.Ordinal) == true)
            {
                SkillPostCount++;
                return Task.FromResult(_skillDenied);
            }

            PresentedCapability = request.Headers.GetValues("X-Weave-Capability").Single();
            return Task.FromResult(_denied);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _health.Dispose();
                _denied.Dispose();
                _started.Dispose();
                _skillDenied.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TestConfigSource : ISystemConfigSource
    {
        public SystemConfigSnapshot Load() => new()
        {
            Version = "1.0",
            BaseUrl = "https://example.test",
            DefaultPort = 9401,
            Storage = "test",
            AuthMode = "none",
            RequireHttps = true,
            SiloPath = null,
            WeaveHome = "test"
        };
    }

    private sealed class TestWorkspaceRegistry : IWorkspaceRegistry
    {
        public void Register(string name, string absolutePath) { }
        public void Unregister(string name) { }
        public string? Resolve(string name) => null;
        public IReadOnlyDictionary<string, string> GetAll() => new Dictionary<string, string>();
        public IEnumerable<string> GetNames() => [];
    }
}
