using FluentAssertions;
using Weave.Deploy;
using Weave.Deploy.Translators;
using Weave.Workspaces.Models;
using Xunit;

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

        result.Success.Should().BeTrue();
        result.GeneratedFiles.Should().ContainSingle();

        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0]);
        content.Should().Contain("services:");
        content.Should().Contain("weave-silo:");
        content.Should().Contain("redis:");
        content.Should().Contain("tool-web-search:");
    }

    [Fact]
    public async Task KubernetesPublisher_GeneratesMultipleFiles()
    {
        var publisher = new KubernetesPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir });

        result.Success.Should().BeTrue();
        result.GeneratedFiles.Should().HaveCountGreaterThan(2);

        var nsFile = result.GeneratedFiles.First(f => f.Contains("namespace"));
        var nsContent = await File.ReadAllTextAsync(nsFile);
        nsContent.Should().Contain("weave-test-workspace");
    }

    [Fact]
    public async Task NomadPublisher_GeneratesHclFile()
    {
        var publisher = new NomadPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir });

        result.Success.Should().BeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0]);
        content.Should().Contain("job \"weave-test-workspace\"");
        content.Should().Contain("driver = \"docker\"");
    }

    [Fact]
    public async Task FlyIoPublisher_GeneratesTomlFile()
    {
        var publisher = new FlyIoPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir });

        result.Success.Should().BeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0]);
        content.Should().Contain("app = \"weave-test-workspace\"");
        content.Should().Contain("primary_region");
    }

    [Fact]
    public async Task GitHubActionsPublisher_GeneratesWorkflowFile()
    {
        var publisher = new GitHubActionsPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir });

        result.Success.Should().BeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0]);
        content.Should().Contain("runs-on: ubuntu-latest");
        content.Should().Contain("researcher");
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }
}
