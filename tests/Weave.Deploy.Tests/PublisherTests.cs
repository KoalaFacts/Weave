using Weave.Deploy.Translators;
using Weave.Workspaces.Models;

namespace Weave.Deploy.Tests;

public sealed class PublisherTests : IDisposable
{
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"weave-test-{Guid.NewGuid():N}");

    private static WorkspaceManifest CreateTestManifest() => new()
    {
        Name = "test-workspace",
        Version = "1.0",
        Workspace = new WorkspaceConfig
        {
            Network = new NetworkConfig { Name = "weave-test" }
        },
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["researcher"] = new() { Model = "claude-sonnet-4-20250514", Tools = ["web-search"] }
        },
        Tools = new Dictionary<string, ToolDefinition>
        {
            ["web-search"] = new() { Type = "mcp" }
        }
    };

    [Fact]
    public async Task DockerComposePublisher_GeneratesValidYaml()
    {
        var publisher = new DockerComposePublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir });

        result.Success.ShouldBeTrue();
        result.GeneratedFiles.Count.ShouldBe(1);

        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0]);
        content.ShouldContain("services:");
        content.ShouldContain("weave-silo:");
        content.ShouldContain("redis:");
        content.ShouldContain("tool-web-search:");
    }

    [Fact]
    public async Task KubernetesPublisher_GeneratesMultipleFiles()
    {
        var publisher = new KubernetesPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir });

        result.Success.ShouldBeTrue();
        result.GeneratedFiles.Count.ShouldBeGreaterThan(2);

        var nsFile = result.GeneratedFiles.First(f => f.Contains("namespace"));
        var nsContent = await File.ReadAllTextAsync(nsFile);
        nsContent.ShouldContain("weave-test-workspace");
    }

    [Fact]
    public async Task NomadPublisher_GeneratesHclFile()
    {
        var publisher = new NomadPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir });

        result.Success.ShouldBeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0]);
        content.ShouldContain("job \"weave-test-workspace\"");
        content.ShouldContain("driver = \"docker\"");
    }

    [Fact]
    public async Task FlyIoPublisher_GeneratesTomlFile()
    {
        var publisher = new FlyIoPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir });

        result.Success.ShouldBeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0]);
        content.ShouldContain("app = \"weave-test-workspace\"");
        content.ShouldContain("primary_region");
    }

    [Fact]
    public async Task GitHubActionsPublisher_GeneratesWorkflowFile()
    {
        var publisher = new GitHubActionsPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir });

        result.Success.ShouldBeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0]);
        content.ShouldContain("runs-on: ubuntu-latest");
        content.ShouldContain("researcher");
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }
}
