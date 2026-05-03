using System.Globalization;
using Weave.Deploy.Translators;
using Weave.Shared;
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

        [Fact]
        public async Task WorkspaceNameAppearsInEnvironment()
        {
            var publisher = new DockerComposePublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("WEAVE_WORKSPACE=test-workspace");
        }

        [Fact]
        public async Task EmptyToolsDictionary_OmitsToolSections()
        {
            var manifest = new WorkspaceManifest
            {
                Name = "empty-tools",
                Version = "1.0",
                Tools = new Dictionary<string, ToolDefinition>()
            };
            var publisher = new DockerComposePublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldNotContain("tool-");
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

        [Fact]
        public async Task NamespaceReferencesWorkspaceName()
        {
            var publisher = new KubernetesPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var nsFile = result.GeneratedFiles.First(f => f.Contains("namespace"));
            var content = await File.ReadAllTextAsync(nsFile, TestContext.Current.CancellationToken);
            content.ShouldContain("name: weave-test-workspace");
        }

        [Fact]
        public async Task SiloDeployment_ContainsRedisConnectionEnv()
        {
            var publisher = new KubernetesPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var siloFile = result.GeneratedFiles.First(f => f.Contains("silo-deployment"));
            var content = await File.ReadAllTextAsync(siloFile, TestContext.Current.CancellationToken);
            content.ShouldContain("REDIS_CONNECTION");
            content.ShouldContain("redis.weave-test-workspace.svc.cluster.local");
        }

        [Fact]
        public async Task CustomRegistry_AppearsInSiloImage()
        {
            var publisher = new KubernetesPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir, Registry = "myregistry.io/org" }, TestContext.Current.CancellationToken);

            var siloFile = result.GeneratedFiles.First(f => f.Contains("silo-deployment"));
            var content = await File.ReadAllTextAsync(siloFile, TestContext.Current.CancellationToken);
            content.ShouldContain("myregistry.io/org/weave-silo:latest");
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

        [Fact]
        public async Task NullScaling_UsesDefaults()
        {
            var manifest = CreateTestManifest() with
            {
                Targets = new Dictionary<string, TargetDefinition>
                {
                    ["production"] = new() { Runtime = "fly-io", Region = "lhr", Scaling = null }
                }
            };
            var publisher = new FlyIoPublisher();
            var result = await publisher.PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("primary_region = \"lhr\"");
            content.ShouldContain("min_machines_running = 1");
            content.ShouldContain("max_count = 10");
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

        [Fact]
        public async Task WorkflowNameIncludesWorkspaceName()
        {
            var publisher = new GitHubActionsPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("name: Weave - test-workspace");
        }

        [Fact]
        public async Task WorkflowFileNameIncludesWorkspaceName()
        {
            var publisher = new GitHubActionsPublisher();
            var result = await publisher.PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.GeneratedFiles[0].ShouldEndWith("weave-test-workspace.yml");
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

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task NullAgents_Succeeds(string name)
        {
            var manifest = new WorkspaceManifest { Name = "no-agents", Version = "1.0" };
            var result = await Resolve(name).PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            result.GeneratedFiles.ShouldNotBeEmpty();
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task NullTools_Succeeds(string name)
        {
            var manifest = new WorkspaceManifest { Name = "no-tools", Version = "1.0" };
            var result = await Resolve(name).PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            result.GeneratedFiles.ShouldNotBeEmpty();
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task NullTargets_Succeeds(string name)
        {
            var manifest = CreateTestManifest() with { Targets = null! };
            var result = await Resolve(name).PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task EmptyTargets_Succeeds(string name)
        {
            var manifest = CreateTestManifest() with { Targets = new Dictionary<string, TargetDefinition>() };
            var result = await Resolve(name).PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task WorkspaceNameAppearsInOutput(string name)
        {
            var manifest = CreateTestManifest();
            var result = await Resolve(name).PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            foreach (var file in result.GeneratedFiles)
            {
                var content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
                content.ShouldContain("test-workspace", customMessage: $"File {Path.GetFileName(file)} should reference the workspace name");
            }
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task PortConstants_AppearInOutput(string name)
        {
            var manifest = CreateTestManifest();
            var result = await Resolve(name).PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);

            var allContent = string.Join("\n", await Task.WhenAll(
                result.GeneratedFiles.Select(f => File.ReadAllTextAsync(f, TestContext.Current.CancellationToken))));

            // Every publisher should reference at least one WeavePorts constant.
            var knownPorts = new[] { WeavePorts.SiloHttp, WeavePorts.OrleansSilo, WeavePorts.OrleansGateway, WeavePorts.Redis };
            knownPorts.ShouldContain(
                p => allContent.Contains(p.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                customMessage: $"{name}: should reference at least one WeavePorts constant");
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task RepublishToSameDirectory_OverwritesCleanly(string name)
        {
            var manifest = CreateTestManifest();
            var opts = new PublishOptions { OutputPath = OutputDir };

            var first = await Resolve(name).PublishAsync(manifest, opts, TestContext.Current.CancellationToken);
            first.Success.ShouldBeTrue();

            var second = await Resolve(name).PublishAsync(manifest, opts, TestContext.Current.CancellationToken);
            second.Success.ShouldBeTrue();
            second.GeneratedFiles.Count.ShouldBe(first.GeneratedFiles.Count);

            foreach (var file in second.GeneratedFiles)
                File.Exists(file).ShouldBeTrue();
        }

        [Theory]
        [MemberData(nameof(TargetNames))]
        public async Task RegistryIgnored_WhenPublisherDoesNotUseIt(string name)
        {
            // Kubernetes uses Registry, the others should produce consistent
            // output regardless of the Registry option.
            if (name == "kubernetes")
                return;

            var manifest = CreateTestManifest();
            var withoutRegistry = await Resolve(name).PublishAsync(manifest, new PublishOptions { OutputPath = Path.Combine(OutputDir, "a") }, TestContext.Current.CancellationToken);
            var withRegistry = await Resolve(name).PublishAsync(manifest, new PublishOptions { OutputPath = Path.Combine(OutputDir, "b"), Registry = "custom.io/org" }, TestContext.Current.CancellationToken);

            for (var i = 0; i < withoutRegistry.GeneratedFiles.Count; i++)
            {
                var a = await File.ReadAllTextAsync(withoutRegistry.GeneratedFiles[i], TestContext.Current.CancellationToken);
                var b = await File.ReadAllTextAsync(withRegistry.GeneratedFiles[i], TestContext.Current.CancellationToken);
                a.ShouldBe(b, customMessage: $"{name}: Registry should not affect output");
            }
        }
    }

    // ── Port constant consistency ──────────────────────────────────

    public sealed class PortConsistency : PublisherTestBase
    {
        [Fact]
        public async Task DockerCompose_EmitsSiloAndRedisPorts()
        {
            var result = await new DockerComposePublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain($"{WeavePorts.SiloHttp}:{WeavePorts.SiloHttp}");
            content.ShouldContain($"{WeavePorts.OrleansSilo}:{WeavePorts.OrleansSilo}");
            content.ShouldContain($"{WeavePorts.OrleansGateway}:{WeavePorts.OrleansGateway}");
            content.ShouldContain($"{WeavePorts.Redis}:{WeavePorts.Redis}");
        }

        [Fact]
        public async Task Kubernetes_EmitsContainerPortsAndRedisConnection()
        {
            var result = await new KubernetesPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var siloFile = result.GeneratedFiles.First(f => f.Contains("silo-deployment"));
            var content = await File.ReadAllTextAsync(siloFile, TestContext.Current.CancellationToken);
            content.ShouldContain($"containerPort: {WeavePorts.SiloHttp}");
            content.ShouldContain($"containerPort: {WeavePorts.OrleansSilo}");
            content.ShouldContain($"containerPort: {WeavePorts.OrleansGateway}");
            content.ShouldContain($"redis.weave-test-workspace.svc.cluster.local:{WeavePorts.Redis}");
        }

        [Fact]
        public async Task Nomad_EmitsStaticPortsAndDaprAppPort()
        {
            var result = await new NomadPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain($"static = {WeavePorts.SiloHttp}");
            content.ShouldContain($"static = {WeavePorts.OrleansSilo}");
            content.ShouldContain($"static = {WeavePorts.OrleansGateway}");
            content.ShouldContain($"static = {WeavePorts.Redis}");
            content.ShouldContain($"\"--app-port\", \"{WeavePorts.SiloHttp}\"");
        }

        [Fact]
        public async Task FlyIo_EmitsInternalPort()
        {
            var result = await new FlyIoPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain($"internal_port = {WeavePorts.SiloHttp}");
            content.ShouldContain("force_https = true");
        }

        [Fact]
        public async Task GitHubActions_EmitsRedisPortMapping()
        {
            var result = await new GitHubActionsPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain($"{WeavePorts.Redis}:{WeavePorts.Redis}");
            content.ShouldContain($"REDIS_CONNECTION: localhost:{WeavePorts.Redis}");
        }
    }

    // ── Publisher-specific edge cases ──────────────────────────────

    public sealed class NomadEdgeCases : PublisherTestBase
    {
        [Fact]
        public async Task EmitsWorkspaceEnvironmentVariable()
        {
            var result = await new NomadPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("WEAVE_WORKSPACE = \"test-workspace\"");
        }

        [Fact]
        public async Task EmitsResourceLimits()
        {
            var result = await new NomadPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("cpu    = 500");
            content.ShouldContain("memory = 512");
        }
    }

    public sealed class KubernetesEdgeCases : PublisherTestBase
    {
        [Fact]
        public async Task ZeroReplicas_EmittedInOutput()
        {
            var manifest = CreateTestManifest() with
            {
                Targets = new Dictionary<string, TargetDefinition>
                {
                    ["staging"] = new() { Runtime = "kubernetes", Replicas = 0 }
                }
            };
            var result = await new KubernetesPublisher().PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles.First(f => f.Contains("silo-deployment")), TestContext.Current.CancellationToken);
            content.ShouldContain("replicas: 0");
        }

        [Fact]
        public async Task RegistryWithTrailingSlash_ProducesValidImage()
        {
            var result = await new KubernetesPublisher().PublishAsync(
                CreateTestManifest(),
                new PublishOptions { OutputPath = OutputDir, Registry = "myregistry.io/" },
                TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles.First(f => f.Contains("silo-deployment")), TestContext.Current.CancellationToken);
            // Even with trailing slash, the image path should contain the registry.
            content.ShouldContain("myregistry.io/");
            content.ShouldContain("weave-silo:latest");
        }

        [Fact]
        public async Task ServiceFile_ExposesAllOrleansPorts()
        {
            var result = await new KubernetesPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles.First(f => f.Contains("silo-service")), TestContext.Current.CancellationToken);
            content.ShouldContain($"port: {WeavePorts.SiloHttp}");
            content.ShouldContain($"port: {WeavePorts.OrleansSilo}");
            content.ShouldContain($"port: {WeavePorts.OrleansGateway}");
        }
    }

    public sealed class GitHubActionsEdgeCases : PublisherTestBase
    {
        [Fact]
        public async Task EmptyAgentsDictionary_OmitsAgentSteps()
        {
            var manifest = new WorkspaceManifest
            {
                Name = "empty-agents",
                Version = "1.0",
                Agents = new Dictionary<string, AgentDefinition>()
            };
            var result = await new GitHubActionsPublisher().PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldNotContain("weave agent send");
        }

        [Fact]
        public async Task DotNetVersionIsPresent()
        {
            var result = await new GitHubActionsPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("dotnet-version: '10.0.x'");
        }

        [Fact]
        public async Task WorkspaceStartStep_ReferencesManifestName()
        {
            var result = await new GitHubActionsPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("--workspace=test-workspace");
        }
    }

    public sealed class FlyIoEdgeCases : PublisherTestBase
    {
        [Fact]
        public async Task EmitsDockerfileBuildBlock()
        {
            var result = await new FlyIoPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("[build]");
            content.ShouldContain("dockerfile = \"Dockerfile\"");
        }

        [Fact]
        public async Task ZeroMinScale_EmittedInOutput()
        {
            var manifest = CreateTestManifest() with
            {
                Targets = new Dictionary<string, TargetDefinition>
                {
                    ["production"] = new() { Runtime = "fly-io", Scaling = new ScalingConfig { Min = 0, Max = 5 } }
                }
            };
            var result = await new FlyIoPublisher().PublishAsync(manifest, new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("min_machines_running = 0");
            content.ShouldContain("min_count = 0");
            content.ShouldContain("max_count = 5");
        }

        [Fact]
        public async Task VmSizeIsPresent()
        {
            var result = await new FlyIoPublisher().PublishAsync(CreateTestManifest(), new PublishOptions { OutputPath = OutputDir }, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.GeneratedFiles[0], TestContext.Current.CancellationToken);
            content.ShouldContain("size = \"shared-cpu-2x\"");
        }
    }
}
