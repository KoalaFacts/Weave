using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weave.Cli.Commands.Local;

internal sealed class LocalMcpServer(LocalInvocationClient invocations)
{
    private static readonly Dictionary<string, string[]> Fields = new(StringComparer.Ordinal)
    {
        ["read_document"] = ["path"],
        ["submit_write"] = ["invocation_id", "path", "content"],
        ["get_status"] = ["invocation_id"],
        ["resume_write"] = ["invocation_id"]
    };

    public async Task<int> RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false, true), bufferSize: 4096, leaveOpen: true);
        return await RunAsync(reader, writer, ct);
    }

    public async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        var initialized = false;
        var ready = false;
        while (await LocalTextLines.ReadAsync(input, LocalHttp.MaxBytes, discardExcess: false, ct) is { } line)
        {
            if (Encoding.UTF8.GetByteCount(line) > LocalHttp.MaxBytes)
                throw new ArgumentException("MCP input exceeds the client limit.");
            JsonNode? id = null;
            JsonObject reply;
            try
            {
                if (JsonNode.Parse(line) is not JsonObject request)
                    throw new ArgumentException("Invalid JSON-RPC object.");
                id = request["id"]?.DeepClone();
                if (request["jsonrpc"]?.GetValue<string>() != "2.0")
                    throw new ArgumentException("Invalid JSON-RPC request.");
                if (request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
                    throw new ArgumentException("Invalid JSON-RPC method.");
                if (!request.ContainsKey("id"))
                {
                    if (method == "notifications/initialized" && initialized)
                        ready = true;
                    continue;
                }
                if (id is not JsonValue value || !(value.TryGetValue<string>(out _) || value.TryGetValue<long>(out _)))
                    throw new ArgumentException("Invalid JSON-RPC id.");
                JsonObject result;
                if (method == "initialize")
                {
                    initialized = true;
                    result = new JsonObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "weave-governed-files", ["version"] = "1.0" },
                        ["instructions"] = "Use one retained invocation_id UUID for each original write. Pending is not execution. A human reviews that UUID in a separate terminal. Query the same UUID after restart. Only resume when execution_state is NotStarted and approvalState is Approved. NotStarted identifies a matching retained approval without an admitted execution attempt; its invocation-not-found HTTP 404 is expected before first execution. OutcomeUnknown identifies an existing attempt: stop, preserve the UUID and investigate. Unconfirmed also requires stopping. Never approve yourself, submit replacement content, or use a new UUID to bypass rejection, expiry or an unknown outcome."
                    };
                }
                else if (method == "ping")
                    result = new JsonObject();
                else if (!ready)
                    throw new ArgumentException("Complete MCP initialization first.");
                else if (method == "tools/list")
                    result = new JsonObject { ["tools"] = ToolList() };
                else if (method == "tools/call" && request["params"] is JsonObject parameters)
                    result = await CallAsync(parameters, ct);
                else
                    throw new ArgumentException("Unsupported MCP method.");
                reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (Exception failure) when (failure is ArgumentException or JsonException or InvalidOperationException)
            {
                reply = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "Invalid or unsupported bounded MCP request." }
                };
            }
            await output.WriteLineAsync(reply.ToJsonString().AsMemory(), ct);
            await output.FlushAsync(ct);
        }
        return 0;
    }

    private async Task<JsonObject> CallAsync(JsonObject parameters, CancellationToken ct)
    {
        try
        {
            if (parameters["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name)
                || parameters["arguments"] is not JsonObject arguments)
                return ToolResult("Use one of the four business tools with its exact string arguments.", true);
            if (!Fields.TryGetValue(name, out var fields) || arguments.Count != fields.Length
                || !fields.All(field => arguments[field] is JsonValue value && value.TryGetValue<string>(out _)))
                return ToolResult("Use one of the four business tools with its exact string arguments.", true);
            var response = await invocations.CallAsync(name, arguments, ct);
            var error = response["http_status"] is JsonValue status && status.TryGetValue<int>(out var code)
                && code is < 200 or >= 300;
            return ToolResult(response.ToJsonString(), error);
        }
        catch (Exception failure) when (failure is IOException or HttpRequestException or ArgumentException or JsonException or InvalidOperationException
            || failure is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return ToolResult("Request not confirmed. Check the original UUID and current authority. Do not retry with a new UUID or replace the original content.", true);
        }
    }

    private static JsonObject ToolResult(string text, bool error) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error
    };

    private static JsonArray ToolList() => new(Fields.Select(pair => (JsonNode)new JsonObject
    {
        ["name"] = pair.Key,
        ["description"] = pair.Key switch
        {
            "read_document" => "Read a document through Weave, not local disk.",
            "submit_write" => "Submit original path/content once with a retained UUID. Writes require human approval; known UUIDs are queried.",
            "get_status" => "Query the original UUID. execution_state distinguishes NotStarted (matching retained approval, no admitted attempt) from an existing attempt's outcome or Unconfirmed. Preserve raw HTTP evidence. A NotStarted invocation-not-found HTTP 404 is expected before first execution; OutcomeUnknown and Unconfirmed require stopping.",
            _ => "Resume the server-retained original UUID once only when execution_state is NotStarted and approvalState is Approved, using current authority. Approval adds no execution grants. Existing attempts, OutcomeUnknown and Unconfirmed are never replayed."
        },
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject(pair.Value.Select(field => KeyValuePair.Create<string, JsonNode?>(field,
                new JsonObject { ["type"] = "string" }))),
            ["required"] = new JsonArray(pair.Value.Select(field => (JsonNode?)JsonValue.Create(field)).ToArray())
        }
    }).ToArray());

}
