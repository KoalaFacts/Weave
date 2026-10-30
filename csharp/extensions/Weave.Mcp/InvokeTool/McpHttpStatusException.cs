using System.Net;

namespace Weave.Tools.Connectors;

internal sealed class McpHttpStatusException(HttpStatusCode statusCode, int? protocolErrorCode)
    : IOException($"MCP HTTP {(int)statusCode}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public int? ProtocolErrorCode { get; } = protocolErrorCode;
}
