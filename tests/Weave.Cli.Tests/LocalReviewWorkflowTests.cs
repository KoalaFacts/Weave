using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalReviewWorkflowTests
{
    private const string Id = "82c07b3d2a3646e88f2f0b8db07c452b";
    private static readonly string Digest = "approval-v1:" + new string('A', 64);

    [Fact]
    public async Task RunAsync_ConfirmedApproval_QueriesOriginalAndStartsAgentWithoutChatNotification()
    {
        using var flow = new ReviewFlow();
        var exit = await flow.RunAsync();
        exit.ShouldBe(0);
        flow.Launcher.Tasks.Count.ShouldBe(1);
        flow.Launcher.Tasks[0].ShouldContain(Id);
        flow.Launcher.Tasks[0].ShouldContain("get_status");
        flow.Launcher.Tasks[0].ShouldContain("resume_write");
        flow.Launcher.Tasks[0].ShouldNotContain("operator-key");
        flow.Handler.Requests.Any(request => request.Path.EndsWith("/resume", StringComparison.Ordinal)).ShouldBeFalse();
        flow.Handler.Requests.Where(request => request.Method == "GET" && !request.Path.EndsWith("/review", StringComparison.Ordinal))
            .All(request => !request.Operator).ShouldBeTrue();
        flow.Issued.ShouldBeGreaterThanOrEqualTo(2);
        flow.AgentQueries.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Theory]
    [InlineData(false, "approve", true, 1)]
    [InlineData(true, "approve wrong-digest", true, 0)]
    [InlineData(true, "reject", true, 0)]
    [InlineData(true, "approve", false, 0)]
    public async Task RunAsync_NoConfirmedApprovalOrManualReview_NeverStartsAgent(bool interactive, string answer, bool continuation, int exit)
    {
        using var flow = new ReviewFlow { Interactive = interactive, Continue = continuation,
            Answer = answer is "approve" or "reject" ? answer + " " + Digest : answer };
        (await flow.RunAsync()).ShouldBe(exit);
        flow.Launcher.Tasks.ShouldBeEmpty();
        flow.Issued.ShouldBe(0);
        flow.AgentQueries.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_DecisionNotConfirmed_NeverContinues()
    {
        using var flow = new ReviewFlow { DecisionStatus = 403 };
        (await flow.RunAsync()).ShouldBe(1);
        flow.Launcher.Tasks.ShouldBeEmpty();
        flow.Issued.ShouldBe(0);
    }

    [Theory]
    [InlineData("Expired")]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    [InlineData("Pending")]
    public async Task RunAsync_ApprovalChangesBeforeContinuation_NeverStartsAgent(string state)
    {
        using var flow = new ReviewFlow { State = state };
        (await flow.RunAsync()).ShouldBe(1);
        flow.AgentQueries.ShouldBeGreaterThan(0);
        flow.Launcher.Tasks.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Succeeded", 0)]
    [InlineData("Failed", 1)]
    [InlineData("OutcomeUnknown", 1)]
    public async Task RunAsync_ExistingExecution_ReportsOutcomeWithoutStartingAgent(string outcome, int exit)
    {
        using var flow = new ReviewFlow { InvocationStatus = 200, Outcome = outcome };
        (await flow.RunAsync()).ShouldBe(exit);
        flow.Launcher.Tasks.ShouldBeEmpty();
        flow.Handler.Requests.Any(request => request.Path.EndsWith("/approval", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_AgentStatusDenied_DoesNotFallBackToApprovalOrLaunch()
    {
        using var flow = new ReviewFlow { InvocationStatus = 403 };
        (await flow.RunAsync()).ShouldBe(1);
        flow.Launcher.Tasks.ShouldBeEmpty();
        flow.Handler.Requests.Any(request => request.Path.EndsWith("/approval", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Pending", null)]
    [InlineData("Approved", null)]
    [InlineData("Approved", "OutcomeUnknown")]
    [InlineData("Approved", "Failed")]
    public async Task RunAsync_AgentExitsZeroWithoutSuccessfulOutcome_FailsWithoutRetry(string state, string? outcome)
    {
        using var flow = new ReviewFlow();
        flow.Launcher.Run = () => { flow.State = state; flow.Outcome = outcome; flow.InvocationStatus = outcome is null ? 404 : 200; return 0; };
        (await flow.RunAsync()).ShouldBe(1);
        flow.Launcher.Tasks.Count.ShouldBe(1);
        flow.Issued.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Theory]
    [InlineData(false, "3fdd0938f7c0449888d4fabb8e838d50")]
    [InlineData(true, "00000000000000000000000000000000")]
    public async Task RunAsync_UnrecordedOrMissingAttempt_DoesNotReportSuccessfulExecution(bool recorded, string attempt)
    {
        using var flow = new ReviewFlow { Recorded = recorded, Attempt = attempt };
        (await flow.RunAsync()).ShouldBe(1);
        flow.Launcher.Tasks.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_AgentFails_StopsWithoutRetry()
    {
        using var flow = new ReviewFlow();
        flow.Launcher.Run = () => 7;
        (await flow.RunAsync()).ShouldBe(7);
        flow.Launcher.Tasks.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_ContinuationCancelled_PreservesOriginalWithoutRetry()
    {
        using var flow = new ReviewFlow();
        using var cancellation = new CancellationTokenSource();
        flow.Launcher.Run = () => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        await Should.ThrowAsync<OperationCanceledException>(() => flow.RunAsync(cancellation.Token));
        flow.Launcher.Tasks.Count.ShouldBe(1);
        flow.Handler.Requests.Any(request => request.Path.EndsWith("/resume", StringComparison.Ordinal)).ShouldBeFalse();
    }

    private sealed class ReviewFlow : IDisposable
    {
        public LocalHttpFixture Handler { get; }
        public TestLauncher Launcher { get; } = new();
        public int Issued { get; private set; }
        public int AgentQueries { get; private set; }
        public string State { get; set; } = "Approved";
        public string? Outcome { get; set; }
        public bool Recorded { get; set; } = true;
        public string Attempt { get; set; } = "3fdd0938f7c0449888d4fabb8e838d50";
        public int InvocationStatus { get; set; } = 404;
        public int DecisionStatus { get; set; } = 200;
        public string Answer { get; set; } = "approve " + Digest;
        public bool Interactive { get; set; } = true;
        public bool Continue { get; set; } = true;
        private readonly HttpClient _client;

        public ReviewFlow()
        {
            Handler = new LocalHttpFixture((request, _) =>
            {
                var route = request.RequestUri!.AbsolutePath;
                if (route.EndsWith("/issue", StringComparison.Ordinal))
                {
                    Issued++;
                    var issued = LocalHttpFixture.Response(200);
                    issued.Content = new StringContent("fresh-agent-" + Issued, Encoding.UTF8, "text/plain");
                    issued.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                    return issued;
                }
                if (route.EndsWith("/review", StringComparison.Ordinal))
                    return LocalHttpFixture.Response(200, new JsonObject
                    {
                        ["invocationId"] = Id, ["workspaceId"] = "onboarding", ["toolName"] = "files", ["operation"] = "write_file",
                        ["planDigest"] = Digest, ["subject"] = "document-agent", ["targetDescription"] = "document root",
                        ["expiresAt"] = "2030-01-01T00:00:00Z", ["parameters"] = new JsonObject { ["path"] = "checklist.md" },
                        ["rawInput"] = "A proposal awaiting a human decision."
                    });
                if (route.EndsWith("/decision", StringComparison.Ordinal))
                    return LocalHttpFixture.Response(DecisionStatus, new JsonObject { ["invocationId"] = Id,
                        ["approvalState"] = Answer.StartsWith("reject", StringComparison.Ordinal) ? "Rejected" : "Approved" });
                AgentQueries++;
                request.Headers.GetValues("X-Weave-Capability").Single().ShouldBe("fresh-agent-" + Issued);
                request.Headers.Contains("X-Weave-Operator-Key").ShouldBeFalse();
                if (route.EndsWith("/approval", StringComparison.Ordinal))
                    return LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = Id, ["approvalState"] = State });
                return LocalHttpFixture.Response(InvocationStatus, Outcome is null ? null : new JsonObject
                {
                    ["invocationId"] = Id, ["toolName"] = "files", ["attemptId"] = Attempt,
                    ["success"] = Outcome == "Succeeded", ["outcome"] = Outcome, ["outcomeRecorded"] = Recorded
                });
            });
            _client = new HttpClient(Handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
            Launcher.Run = () => { InvocationStatus = 200; Outcome = "Succeeded"; return 0; };
        }

        public Task<int> RunAsync(CancellationToken? ct = null) => new LocalReviewWorkflow(new LocalReview(new TestTerminal(Interactive, Answer)), Launcher)
            .RunAsync(new LocalHttp(_client, TimeProvider.System), "private", new LocalDeployment("host", "documents", "onboarding", 9401),
                Id, "reviewer", "operator-key", Continue, "codex", "agent", ct ?? TestContext.Current.CancellationToken);

        public void Dispose() => _client.Dispose();
    }

    private sealed class TestTerminal(bool interactive, string answer) : ILocalReviewConsole
    {
        public bool IsInteractive => interactive;
        public void WriteLine(string text) { }
        public ValueTask<string?> ReadLineAsync(CancellationToken ct) => ValueTask.FromResult<string?>(answer);
    }

    private sealed class TestLauncher : ILocalCodexLauncher
    {
        public List<string> Tasks { get; } = [];
        public Func<int> Run { get; set; } = () => 0;
        public Task<int> RunAsync(string directory, LocalDeployment deployment, string executable,
            string agentDirectory, string? task, bool execute, CancellationToken ct)
        {
            execute.ShouldBeTrue();
            Tasks.Add(task!);
            return Task.FromResult(Run());
        }
    }
}
