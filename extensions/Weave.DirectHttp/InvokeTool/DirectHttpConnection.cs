namespace Weave.Tools.Connectors;

internal sealed record DirectHttpConnection(string ToolName, string BaseUrl, string? AuthHeader);
