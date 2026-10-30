using System.Globalization;
using System.Net;
using System.Text;
using Spectre.Console;
using Weave.Actions.Audit;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Collection(nameof(AuditConsoleGroup))]
public sealed class AuditReplayCliCommandTests
{
    [Theory]
    [InlineData("token123-secret-suffix", "token123…")]
    [InlineData("short-id", "short-id")]
    public async Task ExecuteAsync_ExplicitToken_RendersDecisionsWithLiteralMarkupAndShortToken(string token, string displayedToken)
    {
        using var fixture = new AuditFixture();
        fixture.Handler.Respond = _ => Response(200, """
            [
              {"tokenId":"token123-secret-suffix","grant":"tool:[files]","issuedTo":"agent-marker","workspaceId":"workspace-marker","actionContext":"read [document]","outcome":"Allow","timestamp":"2026-01-02T03:04:05Z"},
              {"tokenId":"token123-secret-suffix","grant":"tool:[files]","issuedTo":"agent-marker","workspaceId":"workspace-marker","actionContext":"write [document]","outcome":"Deny","reason":"Missing [permission]","timestamp":"2026-01-02T03:05:05Z"}
            ]
            """.Replace("token123-secret-suffix", token, StringComparison.Ordinal));

        var result = await fixture.Command.ExecuteAsync(new AuditReplayOptions(token, 10), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Handler.Paths.ShouldBe(["/api/audit/capability/" + token]);
        fixture.Output.ShouldContain("Allow");
        fixture.Output.ShouldContain("Deny");
        fixture.Output.ShouldContain("tool:[files]");
        fixture.Output.ShouldContain("read [document]");
        fixture.Output.ShouldContain("write [document]");
        fixture.Output.ShouldContain("Missing [permission]");
        fixture.Output.ShouldContain("—");
        fixture.Output.ShouldContain(displayedToken);
        fixture.Output.ShouldNotContain("secret-suffix");
        fixture.Output.ShouldContain("agent-marker");
        fixture.Output.ShouldContain("workspace-marker");
        fixture.Output.ShouldContain("2 row(s).");
    }

    [Fact]
    public async Task ExecuteAsync_ShortTokenWithoutRows_ReportsEmptyHistory()
    {
        using var fixture = new AuditFixture();
        fixture.Handler.Respond = _ => Response(200, "[]");

        var result = await fixture.Command.ExecuteAsync(new AuditReplayOptions("short-id", 10), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Handler.Paths.ShouldBe(["/api/audit/capability/short-id"]);
        fixture.Output.ShouldContain("No audit rows recorded for token 'short-id'");
        fixture.Output.ShouldNotContain("row(s).");
    }

    [Fact]
    public async Task ExecuteAsync_QueryFails_ReturnsFailureWithQueryError()
    {
        using var fixture = new AuditFixture();
        fixture.Handler.Respond = _ => throw new HttpRequestException("audit-query-marker");

        var result = await fixture.Command.ExecuteAsync(new AuditReplayOptions("known-token", 10), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Output.ShouldContain("Failed to query the silo:");
        fixture.Output.ShouldContain("audit-query-marker");
        fixture.Output.ShouldNotContain("No audit rows");
    }

    [Fact]
    public async Task ExecuteAsync_QueryCancelled_ReturnsCancellationWithoutReportingEmptyHistory()
    {
        using var fixture = new AuditFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.Handler.Respond = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        var result = await fixture.Command.ExecuteAsync(new AuditReplayOptions("known-token", 10), cancellation.Token);

        result.ShouldBe(130);
        fixture.Handler.Paths.ShouldBe(["/api/audit/capability/known-token"]);
        fixture.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NoTokenAndNoRecentRows_UsesRequestedLimitWithoutPrompting()
    {
        using var fixture = new AuditFixture();
        fixture.Handler.Respond = _ => Response(200, "[]");

        var result = await fixture.Command.ExecuteAsync(new AuditReplayOptions(null, 7), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Handler.Paths.ShouldBe(["/api/audit/capability?limit=7"]);
        fixture.Output.ShouldContain("No capability authorization rows on this silo yet.");
    }

    private static HttpResponseMessage Response(int status, string json) => new((HttpStatusCode)status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class AuditFixture : IDisposable
    {
        private readonly IAnsiConsole _original = AnsiConsole.Console;
        private readonly StringWriter _writer = new(CultureInfo.InvariantCulture);
        private readonly HttpClient _client;
        public AuditHandler Handler { get; } = new();
        public AuditReplayCliCommand Command { get; }
        public string Output => _writer.ToString();

        public AuditFixture()
        {
            _client = new HttpClient(Handler) { BaseAddress = new Uri("https://audit.test") };
            Command = new AuditReplayCliCommand(new GetCapabilityAuditByTokenAction(_client), new GetRecentCapabilityAuditAction(_client));
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(_writer)
            });
            console.Profile.Capabilities.Ansi = false;
            console.Profile.Width = 240;
            AnsiConsole.Console = console;
        }

        public void Dispose()
        {
            AnsiConsole.Console = _original;
            _client.Dispose();
            _writer.Dispose();
        }
    }

    private sealed class AuditHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => throw new InvalidOperationException("Unexpected audit query.");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.ShouldBe(HttpMethod.Get);
            Paths.Add(request.RequestUri.ShouldNotBeNull().PathAndQuery);
            return Task.FromResult(Respond(request));
        }
    }
}
