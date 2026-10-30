namespace Weave.Tools.Connectors;

internal sealed class McpRemoteException(int code, string message)
    : InvalidOperationException($"MCP error {code}: {message}")
{
    public int Code { get; } = code;
}
