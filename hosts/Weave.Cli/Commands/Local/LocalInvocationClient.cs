using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Weave.Cli.Commands.Local;

internal sealed class LocalInvocationClient(LocalHttp http, string workspace, string capability, string receipts)
{
    private readonly string _route = $"/api/workspaces/{workspace}/tools/files/invocations";

    public static string NormalizeId(string value)
    {
        if (!(Guid.TryParseExact(value, "N", out var id) || Guid.TryParseExact(value, "D", out id)) || id == Guid.Empty)
            throw new ArgumentException("Use the original nonzero invocation UUID.");
        return id.ToString("N");
    }

    public async Task<JsonObject> StatusAsync(string id, CancellationToken ct)
    {
        id = NormalizeId(id);
        var result = await http.CallAsync(HttpMethod.Get, _route + "/" + id, capability, null, null, ct);
        var approval = result.Status == 404
            ? await http.CallAsync(HttpMethod.Get, _route + "/" + id + "/approval", capability, null, null, ct) : null;
        var status = new JsonObject { ["invocation"] = result.ToNode(), ["approval"] = approval?.ToNode(), ["invocation_id"] = id };
        status["execution_state"] = LocalInvocationStatus.ExecutionState(status, id);
        return status;
    }

    public async Task<JsonObject> CallAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        if (name == "read_document")
        {
            var request = Body(Guid.NewGuid().ToString("N"), "read_file", PathArgument(arguments));
            return (await http.CallAsync(HttpMethod.Post, _route, capability, null, request, ct)).ToNode();
        }
        var id = NormalizeId(arguments["invocation_id"]!.GetValue<string>());
        if (name == "submit_write")
        {
            var request = Body(id, "write_file", PathArgument(arguments));
            request["rawInput"] = arguments["content"]!.GetValue<string>();
            var bytes = Encoding.UTF8.GetBytes(request.ToJsonString());
            if (bytes.Length > LocalHttp.MaxBytes)
                throw new ArgumentException("Proposal exceeds the one MiB client limit.");
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            var receipt = Path.Combine(receipts, id + ".sha256");
            if (File.Exists(receipt) && File.ReadAllText(receipt) != fingerprint)
                throw new ArgumentException("Changed content cannot reuse the original UUID or approval.");
            var state = await StatusAsync(id, ct);
            if (!File.Exists(receipt) && state["approval"]?["http_status"]?.GetValue<int>() == 404)
            {
                Directory.CreateDirectory(receipts);
                using (var file = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await file.WriteAsync(Encoding.ASCII.GetBytes(fingerprint), ct);
                    file.Flush(flushToDisk: true);
                }
                var submitted = (await http.CallAsync(HttpMethod.Post, _route, capability, null, request, ct)).ToNode();
                submitted["invocation_id"] = id;
                return submitted;
            }
            return state;
        }
        var current = await StatusAsync(id, ct);
        if (name == "resume_write" && LocalInvocationStatus.CanResume(current, id))
        {
            var resumed = (await http.CallAsync(HttpMethod.Post, _route + "/" + id + "/resume", capability, null, null, ct)).ToNode();
            resumed["invocation_id"] = id;
            return resumed;
        }
        return current;
    }

    private static string PathArgument(JsonObject arguments)
    {
        var path = arguments["path"]!.GetValue<string>();
        if (path.Length is 0 or > 1024 || path.Contains('\0'))
            throw new ArgumentException("Use a bounded tool-relative document path.");
        return path;
    }

    private static JsonObject Body(string id, string method, string path) => new()
    {
        ["invocationId"] = id,
        ["toolName"] = "files",
        ["method"] = method,
        ["parameters"] = new JsonObject { ["path"] = path }
    };
}
