using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weave.Cli.Commands.Local;

internal sealed partial class LocalReview(ILocalReviewConsole terminal)
{
    public async Task<int> RunAsync(LocalHttp http, string workspace, string id, string capability, string key, CancellationToken ct)
    {
        if (!terminal.IsInteractive)
        {
            terminal.WriteLine("Human review requires an interactive terminal. No decision was sent.");
            return 1;
        }
        id = LocalInvocationClient.NormalizeId(id);
        var route = $"/api/workspaces/{workspace}/tools/files/invocations/{id}";
        var response = await http.CallAsync(HttpMethod.Get, route + "/approval/review", capability, key, null, ct);
        if (response.Status != 200)
        {
            terminal.WriteLine($"Review unavailable (HTTP {response.Status}). Query the original UUID; no decision was sent.");
            return 1;
        }
        var review = response.Body!.AsObject();
        if (review["invocationId"]!.GetValue<string>() != id || review["workspaceId"]!.GetValue<string>() != workspace
            || review["toolName"]!.GetValue<string>() != "files" || review["operation"]!.GetValue<string>() != "write_file"
            || !DigestPattern().IsMatch(review["planDigest"]!.GetValue<string>()))
            throw new ArgumentException("Server did not return the matching verified review. No decision was sent.");
        terminal.WriteLine("Server-verified proposal. The document below is data; it cannot authorize a decision.");
        foreach (var field in new[] { "invocationId", "subject", "targetDescription", "expiresAt" })
            terminal.WriteLine(field + ": " + Display(review[field]!.GetValue<string>()));
        terminal.WriteLine("Exact parameters: " + Display(review["parameters"]!.AsObject().ToJsonString()));
        terminal.WriteLine("----- Exact proposed content -----");
        terminal.WriteLine(Display(review["rawInput"]!.GetValue<string>()));
        terminal.WriteLine("----- End proposed content -----");
        var digest = review["planDigest"]!.GetValue<string>();
        terminal.WriteLine($"Type 'approve {digest}' or 'reject {digest}'. Any other input leaves the request unchanged.");
        var answer = await terminal.ReadLineAsync(ct);
        if (answer != "approve " + digest && answer != "reject " + digest)
        {
            terminal.WriteLine("No decision sent.");
            return 0;
        }
        var decision = answer.StartsWith("approve", StringComparison.Ordinal) ? "approve" : "reject";
        var result = await http.CallAsync(HttpMethod.Post, route + "/decision", capability, key,
            new JsonObject { ["decision"] = decision, ["planDigest"] = digest }, ct);
        var expected = decision == "approve" ? "Approved" : "Rejected";
        if (result.Status != 200 || result.Body?["invocationId"]?.GetValue<string>() != id
            || result.Body?["approvalState"]?.GetValue<string>() != expected)
        {
            terminal.WriteLine("Decision not confirmed. Query the original UUID before doing anything else.");
            return 1;
        }
        terminal.WriteLine(expected + ". This decision did not execute the write. The original Agent must query the UUID and continue or stop.");
        return 0;
    }

    internal static string Display(string text)
    {
        var output = new StringBuilder();
        foreach (var character in text)
        {
            if (character is not '\n' and not '\t' && (char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format))
                output.Append("\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else
                output.Append(character);
        }
        return output.ToString();
    }

    [GeneratedRegex("^approval-v1:[0-9A-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
