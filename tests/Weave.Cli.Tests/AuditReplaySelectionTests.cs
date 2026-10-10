using System.Net;
using Weave.Actions.Audit;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class AuditReplaySelectionTests
{
    [Fact]
    public async Task Replay_SelectsDistinctTokenAndQueriesFullEscapedIdWhileDisplayingOnlyPrefix()
    {
        using var console = new ScriptedSelectionConsole(ConsoleKey.DownArrow, ConsoleKey.Enter);
        using var handler = new SelectionHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://audit.test") };
        var command = new AuditReplayCliCommand(new GetCapabilityAuditByTokenAction(client), new GetRecentCapabilityAuditAction(client));

        var result = await command.ExecuteAsync(new AuditReplayOptions(null, 17), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        console.RemainingKeys.ShouldBe(0);
        handler.Requests.ShouldBe(["/api/audit/capability?limit=17", "/api/audit/capability/token222%3Fprivate-suffix"]);
        console.Output.ShouldContain("Pick a capability token to replay:");
        console.Output.ShouldContain("token111");
        console.Output.ShouldContain("2 rows");
        console.Output.ShouldContain("token222");
        console.Output.ShouldContain("1 rows");
        console.Output.ShouldNotContain("private-suffix");
        console.Output.ShouldContain("selected grant");
        console.Output.ShouldContain("selected deny reason");
        console.Output.ShouldContain("Deny");
        console.Output.ShouldContain("1 row(s).");
        console.Output.ShouldContain("agent-second");
        console.Output.ShouldNotContain("unselected grant");
    }

    private sealed class SelectionHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Method.ShouldBe(HttpMethod.Get);
            request.Content.ShouldBeNull();
            var uri = request.RequestUri.ShouldNotBeNull();
            uri.Host.ShouldBe("audit.test");
            Requests.Add(uri.PathAndQuery);
            var json = uri.PathAndQuery switch
            {
                "/api/audit/capability?limit=17" => """
                    [
                      {"tokenId":"token111-private-suffix","issuedTo":"agent-first","workspaceId":"space-first","grant":"unselected grant","actionContext":"read","outcome":"Allow","timestamp":"2026-01-02T03:04:05Z"},
                      {"tokenId":"token111-private-suffix","issuedTo":"agent-first","workspaceId":"space-first","grant":"unselected grant","actionContext":"write","outcome":"Deny","timestamp":"2026-01-02T03:05:05Z"},
                      {"tokenId":"token222?private-suffix","issuedTo":"agent-second","workspaceId":"space-second","grant":"selected grant","actionContext":"write","outcome":"Deny","reason":"selected deny reason","timestamp":"2026-01-02T03:06:05Z"}
                    ]
                    """,
                "/api/audit/capability/token222%3Fprivate-suffix" => """
                    [{"tokenId":"token222?private-suffix","issuedTo":"agent-second","workspaceId":"space-second","grant":"selected grant","actionContext":"write","outcome":"Deny","reason":"selected deny reason","timestamp":"2026-01-02T03:06:05Z"}]
                    """,
                _ => throw new InvalidOperationException("Unexpected audit query: " + uri.PathAndQuery)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
