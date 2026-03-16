using Microsoft.AspNetCore.Components;

namespace Weave.Dashboard.Pages;

public sealed partial class Setup : ComponentBase
{
    private string _runtime = "podman";
    private string _secretsProvider = "env";
    private string _workspaceName = "my-workspace";
    private string _workspacePath = "./workspaces/my-workspace";
}
