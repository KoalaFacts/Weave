using System.Text.Json.Nodes;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class RunWorkspaceSelectionProcessTests
{
    [Theory]
    [InlineData("run-workspace-empty", 0, "suggestion-complete")]
    [InlineData("run-workspace-create", 0, "create-guidance")]
    [InlineData("run-workspace-broken", 1, "broken-rejected")]
    [InlineData("run-workspace-selected", 1, "selected-capability-rejected")]
    public async Task Run_UnresolvedWorkspace_UsesTerminalSelectionWithoutStartingAnUnselectedDeployment(
        string scenario, int exitCode, string stage)
    {
        using var fixture = new InitWizardProcessFixture();
        var registryPath = Path.Join(fixture.Private, "workspaces.json");
        var selectedRoot = Path.Join(fixture.Root, "registered workspace");
        var manifestPath = Path.Join(selectedRoot, "workspace.json");
        var registry = new JsonObject { ["registered-team"] = selectedRoot }.ToJsonString();
        if (scenario != "run-workspace-empty")
            await File.WriteAllTextAsync(registryPath, registry, TestContext.Current.CancellationToken);
        if (scenario == "run-workspace-selected")
        {
            Directory.CreateDirectory(selectedRoot);
            await WorkspaceManifestFile.WriteAsync(manifestPath, new WorkspaceManifest { Name = "selected-team", Version = "1.0" },
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Join(fixture.Root, "empty capability.txt"), " \n", TestContext.Current.CancellationToken);
        }
        var originalManifest = File.Exists(manifestPath) ? await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken) : null;

        using var result = await fixture.RunAsync(scenario, exitCode);

        result.RootElement.GetProperty("stages").EnumerateArray().Select(value => value.GetString()).ShouldContain(stage);
        var output = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        if (scenario == "run-workspace-empty")
        {
            output.ShouldContain("Run: weave workspace new suggested-team --preset coding-assistant");
            output.ShouldContain("Then: weave run suggested-team");
            File.Exists(registryPath).ShouldBeFalse();
            Directory.Exists(Path.Join(fixture.Workspace, "suggested-team")).ShouldBeFalse();
        }
        else
        {
            File.ReadAllText(registryPath).ShouldBe(registry);
            output.ShouldContain(scenario == "run-workspace-broken"
                ? "No workspace.json found in the current directory."
                : "Workspace 'unknown-team' not found.");
        }
        if (originalManifest is not null)
            File.ReadAllText(manifestPath).ShouldBe(originalManifest);
        else
            File.Exists(manifestPath).ShouldBeFalse();
        File.Exists(WorkspaceManifestPaths.GetStatePath(manifestPath)).ShouldBeFalse();
        File.Exists(fixture.ConfigPath).ShouldBeFalse();
        output.ShouldNotContain("Starting server on port");
        output.ShouldNotContain("is running.");
    }
}
