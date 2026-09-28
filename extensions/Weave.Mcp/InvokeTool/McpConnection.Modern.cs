using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weave.Tools.Connectors;

internal sealed partial class McpConnection
{
    private async Task<bool> TryInitializeModernAsync(CancellationToken ct)
    {
        using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_transport.ModernProbeTimeout is { } timeout)
            probeDeadline.CancelAfter(timeout);
        JsonElement result;
        try
        {
            result = await SendRequestAsync("server/discover", paramsNode: null, probeDeadline.Token);
        }
        catch (OperationCanceledException) when (probeDeadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return false;
        }
        catch (McpHttpStatusException error) when (error.StatusCode == System.Net.HttpStatusCode.BadRequest
            && error.ProtocolErrorCode is not (-32020 or -32021 or -32022))
        {
            return false;
        }
        catch (McpRemoteException error) when (error.Code is not (-32020 or -32021 or -32022))
        {
            return false;
        }

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("resultType", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || kind.GetString() != "complete"
            || !result.TryGetProperty("supportedVersions", out var versions)
            || versions.ValueKind != JsonValueKind.Array
            || !versions.EnumerateArray().Any(version => version.ValueKind == JsonValueKind.String
                && version.GetString() == ModernProtocolVersion))
            throw new InvalidOperationException("MCP peer does not support the required modern protocol revision.");

        (ServerName, ServerVersion) = ReadServerIdentity(result);
        _modern = true;
        LogMcpInitialized(_toolName, ServerName ?? "?", ModernProtocolVersion);
        return true;
    }

    private static JsonObject AddModernMetadata(JsonNode? paramsNode)
    {
        var parameters = paramsNode is null ? new JsonObject() : paramsNode as JsonObject
            ?? throw new InvalidOperationException("MCP request parameters must be an object.");
        parameters["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = ModernProtocolVersion,
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject
            {
                ["name"] = ClientName,
                ["version"] = ClientVersion
            }
        };
        return parameters;
    }

    private void ValidateModernResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("resultType", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("MCP modern response is missing its result type.");
        if (kind.GetString() != "complete")
            throw new InvalidOperationException("MCP peer returned an unsupported result type.");

        var (name, version) = ReadServerIdentity(result);
        if (ServerName is not null && name is not null && (!string.Equals(name, ServerName, StringComparison.Ordinal)
            || !string.Equals(version, ServerVersion, StringComparison.Ordinal)))
            throw new InvalidOperationException("MCP server identity changed during this connection.");
    }

    private static (string? Name, string? Version) ReadServerIdentity(JsonElement result)
    {
        if (!result.TryGetProperty("_meta", out var metadata))
            return (null, null);
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("MCP modern server metadata is malformed.");
        if (!metadata.TryGetProperty("io.modelcontextprotocol/serverInfo", out var info))
            return (null, null);
        if (info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || !info.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("MCP modern server identity is malformed.");
        return (name.GetString(), version.GetString());
    }
}
