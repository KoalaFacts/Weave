import com.google.gson.*;
import java.io.*;
import java.nio.charset.StandardCharsets;

public final class EchoServer {
    private static boolean initialized, ready;
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final JsonElement TOOL = JsonParser.parseString("""
        {"name":"echo","description":"Return the supplied text unchanged.",
         "inputSchema":{"type":"object","properties":{"text":{"type":"string"}},
                        "required":["text"],"additionalProperties":false}}
        """);

    private static JsonObject object(Object... pairs) {
        JsonObject result = new JsonObject();
        for (int i = 0; i < pairs.length; i += 2) result.add((String) pairs[i], JSON.toJsonTree(pairs[i + 1]));
        return result;
    }
    private static boolean string(JsonElement value) {
        return value != null && value.isJsonPrimitive() && value.getAsJsonPrimitive().isString();
    }
    private static JsonObject error(JsonElement id, int code, String message) {
        return object("jsonrpc", "2.0", "id", id, "error", object("code", code, "message", message));
    }
    private static JsonObject handle(JsonElement value) {
        if (!value.isJsonObject()) return error(null, -32600, "Invalid request.");
        JsonObject request = value.getAsJsonObject();
        if (!new JsonPrimitive("2.0").equals(request.get("jsonrpc")) || !string(request.get("method")))
            return error(null, -32600, "Invalid request.");
        String method = request.get("method").getAsString();
        if (!request.has("id")) {
            if (method.equals("notifications/initialized") && initialized) ready = true;
            return null;
        }
        JsonElement id = request.get("id");
        if (!(string(id) || id.isJsonPrimitive() && id.getAsJsonPrimitive().isNumber()
                && id.getAsString().matches("-?[0-9]+")))
            return error(null, -32600, "Invalid request ID.");
        JsonElement input = request.has("params") ? request.get("params") : new JsonObject();
        if (!input.isJsonObject()) return error(id, -32602, "Expected object parameters.");
        JsonObject params = input.getAsJsonObject();
        JsonObject result;
        if (method.equals("initialize")) {
            initialized = true;
            result = object("protocolVersion", "2024-11-05", "capabilities", object("tools", object()),
                            "serverInfo", object("name", "echo-plugin", "version", "0.1.0"));
        } else if (method.equals("ping")) {
            result = object();
        } else if (!ready) {
            return error(id, -32002, "Server not initialized.");
        } else if (method.equals("tools/list")) {
            JsonArray tools = new JsonArray(); tools.add(TOOL);
            result = object("tools", tools);
        } else if (method.equals("tools/call")) {
            if (!new JsonPrimitive("echo").equals(params.get("name"))) return error(id, -32602, "Unknown tool.");
            JsonElement args = params.get("arguments");
            boolean valid = args != null && args.isJsonObject() && args.getAsJsonObject().size() == 1
                            && string(args.getAsJsonObject().get("text"));
            // Replace this expression with your own tool's behavior.
            String text = valid ? args.getAsJsonObject().get("text").getAsString()
                                : "Expected exactly one string argument: text.";
            JsonArray content = new JsonArray(); content.add(object("type", "text", "text", text));
            result = object("content", content, "isError", !valid);
        } else {
            return error(id, -32601, "Unknown method.");
        }
        return object("jsonrpc", "2.0", "id", id, "result", result);
    }

    public static void main(String[] args) throws IOException {
        var input = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        var output = new PrintWriter(new OutputStreamWriter(System.out, StandardCharsets.UTF_8), true);
        for (String line; (line = input.readLine()) != null;) {
            JsonObject response;
            try { response = handle(JsonParser.parseString(line)); }
            catch (JsonParseException invalid) { response = error(null, -32700, "Invalid JSON."); }
            if (response != null) output.println(JSON.toJson(response));
        }
    }
}
