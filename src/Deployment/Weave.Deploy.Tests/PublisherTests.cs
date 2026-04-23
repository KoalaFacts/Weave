using Weave.Deploy.Translators;
using Weave.Workspaces.Models;

namespace Weave.Deploy.Tests;

/// <summary>
/// One publisher per nested class — matches the shape of the code
/// under test and keeps related scenarios together without needing
/// comment banners or `#region` directives.
/// </summary>
public static class PublisherTests
{
    // ── Shared fixture ─────────────────────────────────────────────

    public abstract class PublisherTestBase : IDisposable
    {
        protected string OutputDir { get; } = Path.Combine(Path.GetTempPath(), $"weave-test-{Guid.NewGuid():N}");

        protected static WorkspaceManifest CreateTestManifest() => new()
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

        public void Dispose()
        {
            if (Directory.Exists(OutputDir))
                Directory.Delete(OutputDir, recursive: true);
            GC.SuppressFinalize(this);
        }
    }

    // ── Docker Compose ─────────────────────────────────────────────

    public sealed class DockerCompose : PublisherTestBase
    {
        [Fact]
        public async Task GeneratesValidYaml()
        {
            var publisher = new DockerComposePublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            result.GeneratedFiles.Count.ShouldBe(1);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("services:");
            content.ShouldContain("weave-silo:");
            content.ShouldContain("redis:");
            content.ShouldContain("tool-web-search:");
        }

        [Fact]
        public async Task WithNoTools_OmitsToolSections()
        {
            var manifest = new WorkspaceManifest
            {
                Name = "no-tools-workspace",
                Version = "1.0",
                Workspace = new WorkspaceConfig { Network = new NetworkConfig { Name = "weave-net" } }
            };
            var publisher = new DockerComposePublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("services:");
            content.ShouldContain("weave-silo:");
            content.ShouldContain("redis:");
            content.ShouldNotContain("tool-");
        }

        [Fact]
        public async Task WithoutNetworkConfig_UsesDefaultName()
        {
            var manifest = new WorkspaceManifest { Name = "default-net", Version = "1.0" };
            var publisher = new DockerComposePublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("weave-network");
            content.ShouldContain("driver: bridge");
        }

        [Fact]
        public async Task MultipleTools_EmitHardenedSectionPerTool()
        {
            var manifest = new WorkspaceManifest
            {
                Name = "multi-tool",
                Version = "1.0",
                Workspace = new WorkspaceConfig { Network = new NetworkConfig { Name = "weave-multi" } },
                Tools = new Dictionary<string, ToolDefinition>
                {
                    ["web-search"] = new() { Type = "mcp" },
                    ["file-ops"] = new() { Type = "cli" },
                    ["db"] = new() { Type = "direct-http" }
                }
            };
            var publisher = new DockerComposePublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("tool-web-search:");
            content.ShouldContain("tool-file-ops:");
            content.ShouldContain("tool-db:");

            // Security hardening (read_only + cap_drop ALL) applied per tool.
            var readonlyCount = content.Split("read_only: true").Length - 1;
            readonlyCount.ShouldBe(3);
        }
    }

    // ── Kubernetes ─────────────────────────────────────────────────

    public sealed class Kubernetes : PublisherTestBase
    {
        [Fact]
        public async Task GeneratesFourResources()
        {
            var publisher = new KubernetesPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.GeneratedFiles.Count.ShouldBe(4);
            result.GeneratedFiles.ShouldContain(f => f.EndsWith("namespace.yml"));
            result.GeneratedFiles.ShouldContain(f => f.EndsWith("silo-deployment.yml"));
            result.GeneratedFiles.ShouldContain(f => f.EndsWith("redis.yml"));
            result.GeneratedFiles.ShouldContain(f => f.EndsWith("silo-service.yml"));
        }

        [Fact]
        public async Task NamespaceContainsWorkspaceName()
        {
            var publisher = new KubernetesPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var nsFile = result.GeneratedFiles.First(f => f.Contains("namespace"));
            var content = await File.ReadAllTextAsync(nsFile, TestContext.Current.CancellationToken);
            content.ShouldContain("weave-test-workspace");
        }

        [Fact]
        public async Task UsesCustomRegistry()
        {
            var publisher = new KubernetesPublisher();
            var options = new PublishOptions { OutputPath = OutputDir, Registry = "myregistry.azurecr.io" };
            var result = await publisher.PublishAsync(CreateTestManifest(), options, TestContext.Current.CancellationToken);

            var siloFile = result.GeneratedFiles.First(f => f.Contains("silo-deployment"));
            var content = await File.ReadAllTextAsync(siloFile, TestContext.Current.CancellationToken);
            content.ShouldContain("myregistry.azurecr.io");
        }

        [Fact]
        public async Task WithoutCustomRegistry_DefaultsToGhcr()
        {
            var publisher = new KubernetesPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var siloFile = result.GeneratedFiles.First(f => f.Contains("silo-deployment"));
            var content = await File.ReadAllTextAsync(siloFile, TestContext.Current.CancellationToken);
            content.ShouldContain("ghcr.io/weave/weave-silo:latest");
        }

        [Fact]
        public async Task WithStagingTarget_UsesReplicasFromTarget()
        {
            var manifest = CreateTestManifest() with
            {
                Targets = new Dictionary<string, TargetDefinition>
                {
                    ["staging"] = new() { Runtime = "kubernetes", Replicas = 5 }
                }
            };
            var publisher = new KubernetesPublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var siloFile = result.GeneratedFiles.First(f => f.Contains("silo-deployment"));
            var content = await File.ReadAllTextAsync(siloFile, TestContext.Current.CancellationToken);
            content.ShouldContain("replicas: 5");
        }

        [Fact]
        public async Task WithoutStagingTarget_DefaultsToOneReplica()
        {
            var manifest = CreateTestManifest() with { Targets = new Dictionary<string, TargetDefinition>() };
            var publisher = new KubernetesPublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var siloFile = result.GeneratedFiles.First(f => f.Contains("silo-deployment"));
            var content = await File.ReadAllTextAsync(siloFile, TestContext.Current.CancellationToken);
            content.ShouldContain("replicas: 1");
        }
    }

    // ── Nomad ──────────────────────────────────────────────────────

    public sealed class Nomad : PublisherTestBase
    {
        [Fact]
        public async Task GeneratesHclFile()
        {
            var publisher = new NomadPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("job \"weave-test-workspace\"");
            content.ShouldContain("driver = \"docker\"");
        }

        [Fact]
        public async Task IncludesWorkspaceNameInFileName()
        {
            var publisher = new NomadPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.GeneratedFiles[0].ShouldContain("weave-test-workspace.nomad.hcl");
        }

        [Fact]
        public async Task EmitsSiloGroupAndRedisGroupWithDaprSidecar()
        {
            var publisher = new NomadPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("group \"silo\"");
            content.ShouldContain("group \"redis\"");
            content.ShouldContain("task \"dapr-sidecar\"");
        }
    }

    // ── Fly.io ─────────────────────────────────────────────────────

    public sealed class FlyIo : PublisherTestBase
    {
        [Fact]
        public async Task GeneratesTomlFile()
        {
            var publisher = new FlyIoPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            result.GeneratedFiles[0].ShouldEndWith("fly.toml");
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("app = \"weave-test-workspace\"");
            content.ShouldContain("primary_region");
        }

        [Fact]
        public async Task WithProductionTarget_UsesRegionAndScaling()
        {
            var manifest = CreateTestManifest() with
            {
                Targets = new Dictionary<string, TargetDefinition>
                {
                    ["production"] = new()
                    {
                        Runtime = "fly-io",
                        Region = "syd",
                        Scaling = new ScalingConfig { Min = 3, Max = 25 }
                    }
                }
            };
            var publisher = new FlyIoPublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("primary_region = \"syd\"");
            content.ShouldContain("min_machines_running = 3");
            content.ShouldContain("min_count = 3");
            content.ShouldContain("max_count = 25");
        }

        [Fact]
        public async Task WithoutProductionTarget_UsesDefaultRegionAndScaling()
        {
            var publisher = new FlyIoPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("primary_region = \"iad\"");
            content.ShouldContain("min_machines_running = 1");
            content.ShouldContain("max_count = 10");
        }

        [Fact]
        public async Task EmitsHealthCheckBlock()
        {
            var publisher = new FlyIoPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("[[http_service.checks]]");
            content.ShouldContain("path = \"/health\"");
        }
    }

    // ── GitHub Actions ─────────────────────────────────────────────

    public sealed class GitHubActions : PublisherTestBase
    {
        [Fact]
        public async Task GeneratesWorkflowFileUnderDotGithubWorkflows()
        {
            var publisher = new GitHubActionsPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            result.GeneratedFiles[0].ShouldContain(Path.Combine(".github", "workflows"));
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("runs-on: ubuntu-latest");
            content.ShouldContain("researcher");
        }

        [Fact]
        public async Task WithoutCiTarget_UsesPullRequestDefault()
        {
            var publisher = new GitHubActionsPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("  pull_request:");
        }

        [Fact]
        public async Task WithCiTarget_UsesCustomTrigger()
        {
            var manifest = CreateTestManifest() with
            {
                Targets = new Dictionary<string, TargetDefinition>
                {
                    ["ci"] = new() { Runtime = "github-actions", Trigger = "push" }
                }
            };
            var publisher = new GitHubActionsPublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("  push:");
        }

        [Fact]
        public async Task WithoutAgents_OmitsAgentSteps()
        {
            var manifest = new WorkspaceManifest { Name = "no-agents", Version = "1.0" };
            var publisher = new GitHubActionsPublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("runs-on: ubuntu-latest");
            content.ShouldNotContain("weave agent send");
        }

        [Fact]
        public async Task WithMultipleAgents_EmitsStepPerAgent()
        {
            var manifest = CreateTestManifest() with
            {
                Agents = new Dictionary<string, AgentDefinition>
                {
                    ["planner"] = new() { Model = "claude-sonnet-4-20250514" },
                    ["coder"] = new() { Model = "claude-sonnet-4-20250514" },
                    ["reviewer"] = new() { Model = "claude-sonnet-4-20250514" }
                }
            };
            var publisher = new GitHubActionsPublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("weave agent send planner");
            content.ShouldContain("weave agent send coder");
            content.ShouldContain("weave agent send reviewer");
        }
    }

    // ── Cross-publisher contracts ──────────────────────────────────
    //
    // Rules that must hold for every publisher, regardless of target.
    // Parameterised over the five targets so a new publisher gets
    // contract coverage for free.

    public sealed class CrossPublisher : PublisherTestBase
    {
        public static TheoryData<string> TargetNames => new()
        {
            "docker-compose",
            "kubernetes",
            "nomad",
            "fly-io",
            "github-actions"
        };

        private static IPublisher Resolve(string name) => name switch
        {
            "docker-compose" => new DockerComposePublisher(),
            "kubernetes" => new KubernetesPublisher(),
            "nomad" => new NomadPublisher(),
            "fly-io" => new FlyIoPublisher(),
            "github-actions" => new GitHubActionsPublisher(),
            _ => throw new ArgumentException($"Unknown target: {name}")
        };

        [Theory]
        [MemberData(nameof(TargetNames))]
        public void TargetName_MatchesExpected(string name)
        {
            Resolve(name).TargetName.ShouldBe(name);
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task CancelledToken_ThrowsOperationCanceled(string name)
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(
                () => Resolve(name).PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, cts.Token));
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task CreatesOutputDirectoryIfMissing(string name)
        {
            var nested = Path.Combine(OutputDir, "nested", "sub", "dir");
            Directory.Exists(nested).ShouldBeFalse();

            var result = await Resolve(name).PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = nested }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            Directory.Exists(nested).ShouldBeTrue();
            foreach (var file in result.GeneratedFiles)
                file.ShouldStartWith(nested);
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task Result_CarriesTargetNameAndOutputPath(string name)
        {
            var publisher = Resolve(name);
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.TargetName.ShouldBe(publisher.TargetName);
            result.OutputPath.ShouldBe(OutputDir);
            result.GeneratedFiles.ShouldNotBeEmpty();
        }
    }
}
