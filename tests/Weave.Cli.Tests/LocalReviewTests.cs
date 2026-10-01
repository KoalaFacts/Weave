using System.Text.Json.Nodes;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalReviewTests
{
    private const string Id = "82c07b3d2a3646e88f2f0b8db07c452b";
    private static readonly string Digest = "approval-v1:" + new string('A', 64);
    private static readonly string[] DecisionFields = ["decision", "planDigest"];

    [Theory]
    [InlineData("approve", "Approved")]
    [InlineData("reject", "Rejected")]
    public async Task RunAsync_ExactConfirmation_DecidesOriginalWithoutExecution(string decision, string state)
    {
        var terminal = new ReviewTerminal(true, decision + " " + Digest);
        using var handler = new LocalHttpFixture((request, _) => request.Method == HttpMethod.Get
            ? LocalHttpFixture.Response(200, Preview()) : LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = Id, ["approvalState"] = state }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var exit = await new LocalReview(terminal).RunAsync(new LocalHttp(client, TimeProvider.System), "onboarding", Id, "reviewer", "operator", TestContext.Current.CancellationToken);
        exit.ShouldBe(0);
        terminal.Output.ShouldContain(state + ". This decision did not execute the write. The original Agent must query the UUID and continue or stop.");
        var post = handler.Requests.Single(request => request.Method == "POST");
        post.Path.ShouldEndWith("/" + Id + "/decision");
        var body = post.Body!;
        body.Select(field => field.Key).Order().ShouldBe(DecisionFields);
        body["planDigest"]!.GetValue<string>().ShouldBe(Digest);
        body["decision"]!.GetValue<string>().ShouldBe(decision);
        handler.Requests.Any(request => request.Path.EndsWith("/resume", StringComparison.Ordinal)).ShouldBeFalse();
        terminal.Output.Any(line => line.Contains("摘要", StringComparison.Ordinal)).ShouldBeTrue();
        terminal.Output.Any(line => line.Contains('\x1b')).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, "approve")]
    [InlineData(true, "Approved")]
    [InlineData(true, "approve wrong-digest")]
    public async Task RunAsync_RedirectedOrInexactConfirmation_NeverSendsDecision(bool interactive, string answer)
    {
        var terminal = new ReviewTerminal(interactive, answer);
        using var handler = new LocalHttpFixture((_, _) => LocalHttpFixture.Response(200, Preview()));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        await new LocalReview(terminal).RunAsync(new LocalHttp(client, TimeProvider.System), "onboarding", Id, "reviewer", "operator", TestContext.Current.CancellationToken);
        handler.Requests.Any(request => request.Method == "POST").ShouldBeFalse();
        if (!interactive)
            handler.Requests.ShouldBeEmpty();
    }

    private static JsonObject Preview() => new()
    {
        ["invocationId"] = Id, ["workspaceId"] = "onboarding", ["toolName"] = "files", ["operation"] = "write_file",
        ["planDigest"] = Digest, ["subject"] = "document-agent", ["targetDescription"] = "private document root",
        ["expiresAt"] = "2030-01-01T00:00:00Z", ["parameters"] = new JsonObject { ["path"] = "summary.md" },
        ["rawInput"] = "摘要\n\x1b[2J\u202e untrusted text"
    };

    private sealed class ReviewTerminal(bool interactive, string answer) : ILocalReviewConsole
    {
        public bool IsInteractive => interactive;
        public List<string> Output { get; } = [];
        public void WriteLine(string text) => Output.Add(text);
        public ValueTask<string?> ReadLineAsync(CancellationToken ct) => ValueTask.FromResult<string?>(answer);
    }
}
