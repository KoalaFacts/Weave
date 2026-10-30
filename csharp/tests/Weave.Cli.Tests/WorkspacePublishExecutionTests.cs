using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class WorkspacePublishExecutionTests
{
    [Fact]
    public async Task ExecuteAsync_ExplicitComposeTarget_WritesDeploymentToRequestedDirectory()
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        const string manifest = """{"version":"1.0","name":"publish-marker","workspace":{"network":{"name":"isolated-network"}},"tools":{"reports":{"type":"mcp"}}}""";
        await File.WriteAllTextAsync(path, manifest, TestContext.Current.CancellationToken);
        var output = Path.Join(directory.Root, "deployment");
        var resolver = new FixedManifestResolver(path);
        var command = new WorkspacePublishCliCommand(resolver, new WorkspacePrompt(new RecordingWorkspaceRegistry(), resolver));

        var result = await command.ExecuteAsync(new WorkspacePublishOptions("publish-marker", "docker-compose", output),
            TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        Directory.GetFiles(output).ShouldBe([Path.Join(output, "docker-compose.yml")]);
        var content = await File.ReadAllTextAsync(Path.Join(output, "docker-compose.yml"), TestContext.Current.CancellationToken);
        content.ShouldContain("WEAVE_WORKSPACE=publish-marker");
        content.ShouldContain("  tool-reports:");
        content.ShouldContain("    image: weave-tool-reports:latest");
        content.ShouldContain("    read_only: true");
        content.ShouldContain("      - ALL");
        content.ShouldContain("  isolated-network:");
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe(manifest);
    }

    [Fact]
    public async Task ExecuteAsync_MissingManifest_DoesNotCreateOutputDirectory()
    {
        using var directory = new LocalTestDirectory();
        var output = Path.Join(directory.Root, "deployment");
        var resolver = new FixedManifestResolver(null);
        var command = new WorkspacePublishCliCommand(resolver, new WorkspacePrompt(new RecordingWorkspaceRegistry(), resolver));

        var result = await command.ExecuteAsync(new WorkspacePublishOptions("missing", "docker-compose", output),
            TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        Directory.Exists(output).ShouldBeFalse();
    }
}
