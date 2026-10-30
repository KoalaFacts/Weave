using System.Net;
using System.Text;
using Spectre.Console;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;

namespace Weave.Cli.Tests;

internal sealed class TuiSessionBoundaryFixture : IDisposable
{
    private readonly IAnsiConsole _previousConsole = AnsiConsole.Console;
    private readonly StringWriter _output = new();
    private readonly string _directory = Directory.CreateTempSubdirectory("weave-tui-session-").FullName;
    private readonly HttpClient _client = new(new ChatHandler()) { BaseAddress = new Uri("https://example.test") };
    private readonly Dictionary<string, string> _manifests = new(StringComparer.Ordinal);

    public TuiSessionBoundaryFixture()
    {
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(_output)
        });
        AnsiConsole.Console.Profile.Capabilities.Ansi = false;
        AnsiConsole.Console.Profile.Width = 180;
        Session = new TuiSession(new ManifestResolver(_manifests));
        Chat = new TuiChatSession(new SendMessageStreamingAction(_client), TimeProvider.System);
    }

    public TuiSession Session { get; }
    public TuiChatSession Chat { get; }
    public string Output => _output.ToString();
    public TuiSession CreateUnopenedSession() => new(new ManifestResolver(_manifests));

    public string AddWorkspace(string name, string json)
    {
        var directory = Directory.CreateDirectory(Path.Join(_directory, name)).FullName;
        var path = Path.Join(directory, "workspace.json");
        File.WriteAllText(path, json);
        _manifests[name] = path;
        return path;
    }

    public static void WriteState(string manifestPath, string value)
    {
        var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
        var directory = Path.GetDirectoryName(statePath);
        directory.ShouldNotBeNull();
        Directory.CreateDirectory(directory);
        File.WriteAllText(statePath, value);
    }

    public async Task SeedConversationAsync()
    {
        AddWorkspace("previous", """{"version":"1.0","workspace":{"isolation":"full"},"name":"previous","agents":{"previous-agent":{"model":"test-model"}}}""");
        Session.TryOpen("previous", out _).ShouldBeTrue();
        Session.MarkRunning("previous-id");
        Session.AgentName = "previous-agent";
        await Chat.SendAsync(Session, "retained question", TestContext.Current.CancellationToken);
        Chat.History.Select(message => message.Content).ShouldBe(["retained question", "retained reply"]);
        ResetOutput();
    }

    public TuiAgentSelector CreateSelector(IActionPrompter? prompter = null) => new(
        new TuiAgentNameSource(new ListAgentsAction(_client)),
        new SelectAgentAction(prompter ?? new UnexpectedPrompter()));

    public TuiWorkspaceOpener CreateOpener(IWorkspaceLocator locator, IActionPrompter? prompter = null)
    {
        var watch = new WatchWorkspaceAction(new GetWorkspaceStatusAction(_client),
            new ListAgentsAction(_client), new ListToolsAction(_client));
        return new TuiWorkspaceOpener(new OpenWorkspaceAction(locator, prompter ?? new UnexpectedPrompter()),
            CreateSelector(), new TuiLiveStatusView(watch, new TuiLiveStatusWatcher(watch)));
    }

    public void ResetOutput() => _output.GetStringBuilder().Clear();

    public void Dispose()
    {
        AnsiConsole.Console = _previousConsole;
        _client.Dispose();
        _output.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class ManifestResolver(IReadOnlyDictionary<string, string> manifests) : IManifestResolver
    {
        public string? Resolve(string? workspace) => workspace is null ? null : manifests.GetValueOrDefault(workspace);
    }

    private sealed class UnexpectedPrompter : IActionPrompter
    {
        public Task<string> PromptTextAsync(string message, string? defaultValue = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This scenario should not request text.");

        public Task<string> PromptSelectionAsync(string message, IReadOnlyList<string> choices, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This scenario should not request a selection.");

        public Task<bool> PromptConfirmAsync(string message, bool defaultValue = false, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This scenario should not request confirmation.");
    }

    private sealed class ChatHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath.EndsWith("/messages/stream", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("This fixture permits only the seeded conversation request.");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("event: complete\ndata: {\"content\":\"retained reply\",\"usedTools\":false,\"messages\":[]}\n\n",
                    Encoding.UTF8, "text/event-stream")
            });
        }
    }
}
