using System.Text.Json.Nodes;

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
        if (Succeeded(current, id))
        {
            Console.WriteLine("The original UUID already has a successful recorded outcome. No Agent was started.");
            return 0;
        }
        if (current["invocation"]!["http_status"]!.GetValue<int>() != 404
            || current["approval"]?["http_status"]?.GetValue<int>() != 200
            || current["approval"]?["result"]?["invocationId"]?.GetValue<string>() != id
            || current["approval"]?["result"]?["approvalState"]?.GetValue<string>() != "Approved")
        {
            Console.Error.WriteLine("The original UUID is not Approved without execution. Continuation stopped; query its retained state.");
            return 1;
        }

        Console.WriteLine($"Starting the original Agent subject to query and continue UUID {id} with fresh narrow authority.");
        var task = $"Continue only the retained invocation UUID {id}. First call weave_files.get_status for this exact UUID. "
            + "Only if it is Approved with no invocation outcome, call weave_files.resume_write once for this same UUID. "
            + "The server retains the original content: do not generate content, submit_write, choose another UUID or another target. "
            + "If a result exists, or approval is Pending, Rejected, Expired, Cancelled, or the outcome is unknown, stop and report the status. "
            + "Authentication failures and unconfirmed responses also require stopping. After resuming, query and report this UUID's recorded outcome.";
        var exit = await codex.RunAsync(directory, deployment, executable, agentDirectory, task, execute: true, ct);
        if (exit != 0)
        {
            Console.Error.WriteLine("Agent continuation did not finish normally. Preserve and query the original UUID before any retry.");
            return exit;
        }
        var final = await QueryAsync(http, directory, deployment.Workspace, id, key, ct);
        Console.WriteLine(final.ToJsonString());
        if (Succeeded(final, id))
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
        return await new LocalInvocationClient(http, workspace, freshCapability, Path.Combine(directory, "receipts")).StatusAsync(id, ct);
    }

    private static bool Succeeded(JsonObject status, string id)
    {
        var result = status["invocation"]!["result"];
        return status["invocation"]!["http_status"]!.GetValue<int>() == 200
            && result?["invocationId"]?.GetValue<string>() == id && result?["toolName"]?.GetValue<string>() == "files"
            && result?["success"]?.GetValue<bool>() == true && result?["outcomeRecorded"]?.GetValue<bool>() == true
            && result?["outcome"]?.GetValue<string>() == "Succeeded"
            && Guid.TryParseExact(result?["attemptId"]?.GetValue<string>(), "N", out var attempt) && attempt != Guid.Empty;
    }
}
