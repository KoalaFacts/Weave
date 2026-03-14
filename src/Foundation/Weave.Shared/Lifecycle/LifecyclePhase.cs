namespace Weave.Shared.Lifecycle;

public enum LifecyclePhase
{
    // Workspace lifecycle
    WorkspaceCreating,
    WorkspaceCreated,
    WorkspaceStarting,
    WorkspaceStarted,
    WorkspaceStopping,
    WorkspaceStopped,
    WorkspaceDestroying,
    WorkspaceDestroyed,

    // Agent lifecycle
    AgentActivating,
    AgentActivated,
    AgentDeactivating,
    AgentDeactivated,
    AgentErrored,

    // Tool lifecycle
    ToolConnecting,
    ToolConnected,
    ToolDisconnecting,
    ToolDisconnected,
    ToolInvoking,
    ToolInvoked,
    ToolErrored
}
