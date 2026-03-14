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
        var ct = TestContext.Current.CancellationToken;
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir }, ct);

        result.Success.ShouldBeTrue();
        result.GeneratedFiles.Count.ShouldBe(1);

        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], ct);
        content.ShouldContain("services:");
        content.ShouldContain("weave-silo:");
        content.ShouldContain("redis:");
        content.ShouldContain("tool-web-search:");
    }

    [Fact]
    public async Task KubernetesPublisher_GeneratesMultipleFiles()
    {
        var publisher = new KubernetesPublisher();
        var ct = TestContext.Current.CancellationToken;
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir }, ct);

        result.Success.ShouldBeTrue();
        result.GeneratedFiles.Count.ShouldBeGreaterThan(2);

        var nsFile = result.GeneratedFiles.First(f => f.Contains("namespace"));
        var nsContent = await File.ReadAllTextAsync(nsFile, ct);
        nsContent.ShouldContain("weave-test-workspace");
    }

    [Fact]
    public async Task NomadPublisher_GeneratesHclFile()
    {
        var publisher = new NomadPublisher();
        var ct = TestContext.Current.CancellationToken;
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir }, ct);

        result.Success.ShouldBeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], ct);
        content.ShouldContain("job \"weave-test-workspace\"");
        content.ShouldContain("driver = \"docker\"");
    }

    [Fact]
    public async Task FlyIoPublisher_GeneratesTomlFile()
    {
        var publisher = new FlyIoPublisher();
        var ct = TestContext.Current.CancellationToken;
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir }, ct);

        result.Success.ShouldBeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], ct);
        content.ShouldContain("app = \"weave-test-workspace\"");
        content.ShouldContain("primary_region");
    }

    [Fact]
    public async Task GitHubActionsPublisher_GeneratesWorkflowFile()
    {
        var publisher = new GitHubActionsPublisher();
        var ct = TestContext.Current.CancellationToken;
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir }, ct);

        result.Success.ShouldBeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], ct);
        content.ShouldContain("runs-on: ubuntu-latest");
        content.ShouldContain("researcher");
    }

    [Theory]
    [InlineData("docker-compose", "docker-compose")]
    [InlineData("kubernetes", "kubernetes")]
    [InlineData("nomad", "nomad")]
    [InlineData("fly-io", "fly-io")]
    [InlineData("github-actions", "github-actions")]
    public void AllPublishers_HaveCorrectTargetName(string _, string expectedName)
    {
        IPublisher publisher = expectedName switch
        {
            "docker-compose" => new DockerComposePublisher(),
            "kubernetes" => new KubernetesPublisher(),
            "nomad" => new NomadPublisher(),
            "fly-io" => new FlyIoPublisher(),
            "github-actions" => new GitHubActionsPublisher(),
            _ => throw new ArgumentException($"Unknown target: {expectedName}")
        };

        publisher.TargetName.ShouldBe(expectedName);
    }

    [Fact]
    public async Task DockerComposePublisher_WithNoTools_GeneratesValidYaml()
    {
        var manifest = new WorkspaceManifest
        {
            Name = "no-tools-workspace",
            Version = "1.0",
            Workspace = new WorkspaceConfig
            {
                Network = new NetworkConfig { Name = "weave-net" }
            }
        };
        var publisher = new DockerComposePublisher();
        var ct = TestContext.Current.CancellationToken;
        var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = _outputDir }, ct);

        result.Success.ShouldBeTrue();
        var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], ct);
        content.ShouldContain("services:");
        content.ShouldContain("weave-silo:");
        content.ShouldContain("redis:");
        content.ShouldNotContain("tool-");
    }

    [Fact]
    public async Task KubernetesPublisher_UsesCustomRegistry()
    {
        var publisher = new KubernetesPublisher();
        var options = new PublishOptions
        {
            OutputPath = _outputDir,
            Registry = "myregistry.azurecr.io"
        };
        var ct = TestContext.Current.CancellationToken;
        var result = await publisher.PublishAsync(CreateTestManifest(), options, ct);

        result.Success.ShouldBeTrue();
        var siloFile = result.GeneratedFiles.First(f => f.Contains("silo-deployment"));
        var content = await File.ReadAllTextAsync(siloFile, ct);
        content.ShouldContain("myregistry.azurecr.io");
    }

    [Fact]
    public async Task DockerComposePublisher_CancellationRequested_ThrowsOperationCanceled()
    {
        var manifest = CreateTestManifest();
        var publisher = new DockerComposePublisher();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => publisher.PublishAsync(manifest, new PublishOptions { OutputPath = _outputDir }, cts.Token));
    }

    [Fact]
    public async Task NomadPublisher_IncludesWorkspaceNameInFileName()
    {
        var publisher = new NomadPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir }, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.GeneratedFiles[0].ShouldContain("weave-test-workspace.nomad.hcl");
    }

    [Fact]
    public async Task GitHubActionsPublisher_GeneratesInsideGithubWorkflowsDir()
    {
        var publisher = new GitHubActionsPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir }, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.GeneratedFiles[0].ShouldContain(Path.Combine(".github", "workflows"));
    }

    [Fact]
    public async Task FlyIoPublisher_GeneratesFlyToml()
    {
        var publisher = new FlyIoPublisher();
        var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = _outputDir }, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.GeneratedFiles[0].ShouldEndWith("fly.toml");
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }
}
