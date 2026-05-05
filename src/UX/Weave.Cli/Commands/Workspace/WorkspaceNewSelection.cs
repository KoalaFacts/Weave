using Weave.Workspaces.Manifest;
namespace Weave.Cli.Commands;

internal sealed record WorkspaceNewSelection(
    string Model,
    List<string> Tools,
    string? SelectedPresetName,
    IsolationLevel Isolation,
    IReadOnlyList<string> Capabilities);
