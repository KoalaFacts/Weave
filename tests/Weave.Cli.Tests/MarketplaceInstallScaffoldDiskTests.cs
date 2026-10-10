using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallScaffoldDiskTests
{
    [Fact]
    public Task Install_WritesUsableManifestPromptAndDirectories_AndPersistsRegistration() =>
        SiloLauncherProcessHarness.RunAsync(typeof(MarketplaceInstallScaffoldDiskTests), async root =>
        {
            using var output = new ShellOutputCapture();
            using var fixture = new MarketplaceInstallFlowFixture(new WorkspaceRegistry());
            const string workspaceName = "owned review workspace";
            var directory = Path.Join(root, workspaceName);

            var result = await fixture.Command.ExecuteAsync(
                new MarketplaceInstallOptions("item?owned", WorkspaceName: workspaceName), TestContext.Current.CancellationToken);

            result.ShouldBe(0);
            fixture.AssertInstallRequests();
            new WorkspaceRegistry().Resolve(workspaceName).ShouldBe(directory);
            var parser = new ManifestParser();
            var manifest = parser.ParseFile(Path.Join(directory, "workspace.json"));
            parser.Validate(manifest).ShouldBeEmpty();
            manifest.Name.ShouldBe(workspaceName);
            manifest.Workspace.Isolation.ShouldBe(IsolationLevel.Full);
            manifest.Agents.Keys.ShouldBe(["assistant"]);
            var agent = manifest.Agents["assistant"];
            agent.Model.ShouldBe("fixture-model");
            agent.Provider.ShouldBe("fixture-provider");
            agent.MaxConcurrentTasks.ShouldBe(3);
            agent.Tools.ShouldBe(["documents"]);
            agent.Capabilities.ShouldBe(["tool:documents:invoke:read"]);
            agent.SystemPromptFile.ShouldBe("./prompts/assistant.md");
            manifest.Tools.Keys.ShouldBe(["documents"]);
            manifest.Tools["documents"].Type.ShouldBe("filesystem");
            manifest.Targets["local"].Runtime.ShouldBe("podman");
            File.ReadAllText(Path.Join(directory, "prompts", "assistant.md"))
                .ShouldBe("# Review template [literal]\n\nReview local documents.\n");
            Directory.Exists(Path.Join(directory, "data")).ShouldBeTrue();
            Directory.Exists(Path.Join(directory, ".weave")).ShouldBeTrue();
            output.Text.ShouldContain("Installed: Review bundle [literal]");
            output.Text.ShouldContain("Review template [literal] (template-owned)");
            output.Text.ShouldContain("review, local");
            output.Text.ShouldContain("Workspace \"owned review workspace\" scaffolded");
        });
}
