using System.Text.Json.Nodes;
using Weave.Invocations;

namespace Weave.Cli.Commands.Local;

internal sealed class LocalReviewWorkflow(LocalReview review, ILocalCodexLauncher codex)
{
    public async Task<int> RunAsync(LocalHttp http, string directory, LocalDeployment deployment, string id,
        string capability, string key, bool continueExecution, string executable, string agentDirectory, CancellationToken ct)
    {
        id = LocalInvocationClient.NormalizeId(id);
        var decision = await review.RunAsync(http, deployment.Workspace, id, capability, key, ct);
        if (decision == LocalReviewOutcome.Unavailable)
            return 1;
        if (!continueExecution || decision != LocalReviewOutcome.Approved)
            return 0;

        var current = await QueryAsync(http, directory, deployment.Workspace, id, key, ct);
        if (LocalInvocationStatus.ExecutionState(current, id) == nameof(InvocationOutcome.Succeeded))
        {
            Console.WriteLine("The original UUID already has a successful recorded outcome. No Agent was started.");
            return 0;
        }
        if (!LocalInvocationStatus.CanResume(current, id))
        {
            Console.Error.WriteLine("The original UUID is not Approved without execution. Continuation stopped; query its retained state.");
            return 1;
        }

        Console.WriteLine($"Starting the original Agent subject to query and continue UUID {id} with fresh narrow authority.");
        var task = $"Continue only the retained invocation UUID {id}. First call weave_files.get_status for this exact UUID. "
            + "Only if execution_state is NotStarted and approvalState is Approved, call weave_files.resume_write once for this same UUID. "
            + "NotStarted means the matching retained approval has no admitted execution attempt; the invocation-not-found HTTP 404 is expected before first execution. "
            + "It is distinct from OutcomeUnknown, which belongs to an existing attempt and must never be resumed automatically. "
            + "The server retains the original content: do not generate content, submit_write, choose another UUID or another target. "
            + "If an execution record exists, or approval is Pending, Rejected, Expired, Cancelled or Consumed, or execution_state is OutcomeUnknown or Unconfirmed, stop and report the status. "
            + "Authentication failures and unconfirmed responses also require stopping. After resuming, query and report this UUID's recorded outcome.";
        var exit = await codex.RunAsync(directory, deployment, executable, agentDirectory, task, execute: true, ct);
        if (exit != 0)
        {
            Console.Error.WriteLine("Agent continuation did not finish normally. Preserve and query the original UUID before any retry.");
            return exit;
        }
        var final = await QueryAsync(http, directory, deployment.Workspace, id, key, ct);
        Console.WriteLine(final.ToJsonString());
        if (LocalInvocationStatus.ExecutionState(final, id) == nameof(InvocationOutcome.Succeeded))
        {
            Console.WriteLine("Original UUID execution is confirmed by the Host's recorded successful outcome.");
            return 0;
        }
        Console.Error.WriteLine("No successful recorded outcome was confirmed. The original UUID was preserved; continuation will not be retried automatically.");
        return 1;
    }

    private static async Task<JsonObject> QueryAsync(LocalHttp http, string directory, string workspace, string id, string key, CancellationToken ct)
    {
        var freshCapability = await http.IssueAsync("agent", key, ct);
        return await new LocalInvocationClient(http, workspace, freshCapability, Path.Join(directory, "receipts")).StatusAsync(id, ct);
    }

}
