using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Actors;

internal sealed class ToolActorIdentity
{
    public string WorkspaceId { get; private set; } = string.Empty;
    public string ToolName { get; private set; } = string.Empty;

    public void Activate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var parts = key.Split('/', 2);
        WorkspaceId = parts.Length > 1 ? parts[0] : key;
        ToolName = parts.Length > 1 ? parts[1] : key;
    }

    public void Ensure(ToolSpec? definition = null, CapabilityToken? token = null, ToolInvocation? invocation = null)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId))
        {
            if (token is null || string.IsNullOrWhiteSpace(token.WorkspaceId))
                throw new InvalidOperationException(
                    "ToolActor identity cannot be established. Activate via the runtime "
                    + "(real actor call) or provide a capability token whose WorkspaceId "
                    + "identifies the workspace.");

            WorkspaceId = token.WorkspaceId;
        }

        if (string.IsNullOrWhiteSpace(ToolName))
        {
            var resolved = definition?.Name ?? invocation?.ToolName;
            if (string.IsNullOrWhiteSpace(resolved))
                throw new InvalidOperationException(
                    "ToolActor tool name cannot be established. Provide a ToolSpec "
                    + "definition or a ToolInvocation.");

            ToolName = resolved;
        }
    }
}
