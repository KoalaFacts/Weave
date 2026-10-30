using Weave.Cli.Shell;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

public class WorkspaceManifestPathsTests
{
    [Fact]
    public void GetStatePath_ReturnsSidecarUnderManifestDirectory()
    {
        var manifest = Path.Join(Path.GetTempPath(), "demo", "workspace.json");

        var statePath = WorkspaceManifestPaths.GetStatePath(manifest);

        statePath.ShouldBe(Path.Join(Path.GetDirectoryName(Path.GetFullPath(manifest))!, ".weave", "workspace-id"));
    }

    [Fact]
    public void GetStatePath_ResolvesRelativeManifest_AgainstCurrentDirectory()
    {
        var statePath = WorkspaceManifestPaths.GetStatePath("workspace.json");

        var expected = Path.Join(Directory.GetCurrentDirectory(), ".weave", "workspace-id");
        statePath.ShouldBe(expected);
    }

    [Fact]
    public void PrepareForSilo_RewritesRelativePromptPaths_ToAbsolute()
    {
        var manifestDirectory = Path.Join(Path.GetTempPath(), "demo");
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "demo",
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["assistant"] = new AgentDefinition
                {
                    Model = "gpt-4o-mini",
                    SystemPromptFile = "./prompts/assistant.md"
                }
            }
        };

        var prepared = WorkspaceManifestPaths.PrepareForSilo(manifest, manifestDirectory);

        var expected = Path.GetFullPath(Path.Join(manifestDirectory, "./prompts/assistant.md"));
        prepared.Agents["assistant"].SystemPromptFile.ShouldBe(expected);
    }

    [Fact]
    public void PrepareForSilo_LeavesAbsolutePromptPaths_Unchanged()
    {
        var absolute = Path.Join(Path.GetTempPath(), "templates", "global.md");
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "demo",
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["assistant"] = new AgentDefinition
                {
                    Model = "gpt-4o-mini",
                    SystemPromptFile = absolute
                }
            }
        };

        var prepared = WorkspaceManifestPaths.PrepareForSilo(manifest, Path.Join(Path.GetTempPath(), "demo"));

        prepared.Agents["assistant"].SystemPromptFile.ShouldBe(absolute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PrepareForSilo_LeavesBlankPromptPaths_Unchanged(string? blank)
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "demo",
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["assistant"] = new AgentDefinition
                {
                    Model = "gpt-4o-mini",
                    SystemPromptFile = blank
                }
            }
        };

        var prepared = WorkspaceManifestPaths.PrepareForSilo(manifest, Path.Join(Path.GetTempPath(), "demo"));

        prepared.Agents["assistant"].SystemPromptFile.ShouldBe(blank);
    }
}
