using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed record WorkspaceNewSelection(
    string Model,
    List<string> Tools,
    string? SelectedPresetName,
    IsolationLevel Isolation);
