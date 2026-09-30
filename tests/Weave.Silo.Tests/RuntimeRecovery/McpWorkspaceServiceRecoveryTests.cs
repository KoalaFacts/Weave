using Weave.Agents.ToolRegistry;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.Plugins;
using Weave.Silo.RuntimeRecovery;
using Weave.Tools.InstallDaprTool;
using Weave.Tools.InstallMcpTool;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.Tests.RuntimeRecovery;

public sealed class McpWorkspaceServiceRecoveryTests
{
    [Theory]
    [InlineData("authority")]
    [InlineData("revision")]
    [InlineData("identity")]
    [InlineData("config")]
    [InlineData("unpinned")]
    [InlineData("non-loopback")]
    [InlineData("extra-plugin")]
    [InlineData("duplicate-tool")]
    [InlineData("unsupported-tool")]
    [InlineData("tool-target")]
    public async Task DescribeAsync_UnsupportedOrInvalidBindings_Blocks(string kind)
    {
        var fx = new Fixture();
        var installation = fx.Services.McpInstallations.Single();
        var services = fx.Services;
        switch (kind)
        {
            case "authority":
                installation = installation with { HasUnsupportedAuthority = true };
                break;
            case "revision":
                installation = installation with { DefinitionRevision = "mcp_tools/other" };
                break;
            case "identity":
                installation = installation with { Id = "other/server" };
                break;
            case "config":
                installation = installation with { ConfigDigest = "invalid" };
                break;
            case "unpinned":
                installation = installation with { ContractDigest = "" };
                break;
            case "non-loopback":
                installation = installation with
                {
                    Url = "http://example.invalid/mcp",
                    ConfigDigest = McpToolInstallation.ComputeConfigDigest("http://example.invalid/mcp", "echo", "1", "echo")
                };
                break;
            case "extra-plugin":
                services = services with { Plugins = ["server", "extra"] };
                break;
            case "duplicate-tool":
                services = services with { Tools = ["echo", "echo"] };
                break;
            case "unsupported-tool":
                fx.SetTools(fx.Tool with { Supported = false });
                break;
            case "tool-target":
                fx.SetTools(fx.Tool with { Url = "http://127.0.0.1:5678/mcp" });
                break;
        }
        var result = await fx.Recovery.DescribeAsync(services with { McpInstallations = [installation] }, TestContext.Current.CancellationToken);
        result.BlockReason.ShouldBe("hosted-services-require-restoration");
        result.Digest.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeAsync_InstallationNamesAliasCase_BlocksAmbiguousRouting()
    {
        var fx = new Fixture();
        (await fx.Recovery.DescribeAsync(fx.Services, TestContext.Current.CancellationToken)).BlockReason.ShouldBeNull();
        var installation = fx.Services.McpInstallations.Single();
        var aliases = fx.Services with
        {
            McpInstallations = [installation, installation with { Id = "ws/SERVER", PluginName = "SERVER" }]
        };
        (await fx.Recovery.DescribeAsync(aliases, TestContext.Current.CancellationToken)).BlockReason.ShouldBe("hosted-services-require-restoration");
    }

    [Fact]
    public async Task DescribeAsync_InstallationOrToolRevisionChanges_ChangesFrozenDigest()
    {
        var fx = new Fixture();
        var before = await fx.Recovery.DescribeAsync(fx.Services, TestContext.Current.CancellationToken);
        before.RequiredGrants.ShouldBe(["plugin:invoke:ws/server", "tool:echo:connect"]);
        var changedContract = fx.Services with { McpInstallations = [fx.Services.McpInstallations.Single() with { ContractDigest = new string('b', 64) }] };
        (await fx.Recovery.DescribeAsync(changedContract, TestContext.Current.CancellationToken)).Digest.ShouldNotBe(before.Digest);
        fx.SetTools(fx.Tool with { Digest = new string('d', 64) });
        (await fx.Recovery.DescribeAsync(fx.Services, TestContext.Current.CancellationToken)).Digest.ShouldNotBe(before.Digest);
    }

    [Fact]
    public async Task RestoreAsync_InstallationNotConnected_DoesNotRestoreTools()
    {
        var fx = new Fixture();
        fx.Installations.MatchesInstallation(Arg.Any<McpToolInstallationSnapshot>()).Returns(false);
        (await fx.RestoreAsync()).ShouldBe("mcp-installation-not-restored");
        await fx.Registry.DidNotReceive().RestoreMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreAsync_DisabledDuringFreshProbe_DoesNotConfirm()
    {
        var fx = new Fixture();
        fx.Probe.OnProbe = (_, _) =>
        {
            fx.Installations.MatchesInstallation(Arg.Any<McpToolInstallationSnapshot>()).Returns(false);
            return Task.FromResult(InstallationFailureCode.None);
        };
        (await fx.RestoreAsync()).ShouldBe("mcp-installation-not-restored");
    }

    [Theory]
    [InlineData(InstallationFailureCode.ContractRejected, "mcp-contract-rejected")]
    [InlineData(InstallationFailureCode.PeerUnavailable, "mcp-peer-unavailable")]
    public async Task RestoreAsync_FreshPeerProbeFails_DoesNotConfirm(InstallationFailureCode failure, string reason)
    {
        var fx = new Fixture();
        var probes = 0;
        fx.Probe.OnProbe = (_, _) => Task.FromResult(++probes == 1 ? InstallationFailureCode.None : failure);
        (await fx.RestoreAsync()).ShouldBe(reason);
        await fx.Registry.Received(1).RestoreMcpToolAsync("echo", fx.Tool.Digest, Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
    }

    private sealed class Fixture
    {
        public IToolRegistryActor Registry { get; } = Substitute.For<IToolRegistryActor>();
        public IMcpInstallationDispatchGate Installations { get; } = Substitute.For<IMcpInstallationDispatchGate>();
        public ProbeStub Probe { get; } = new();
        public McpWorkspaceServiceRecovery Recovery { get; }
        public McpToolRecoveryPlan Tool { get; } = new()
        {
            Name = "echo",
            PluginName = "server",
            Url = "http://127.0.0.1:1234/mcp",
            Supported = true,
            Digest = new string('c', 64)
        };
        public WorkspaceHostedServices Services { get; } = new()
        {
            WorkspaceId = "ws",
            Tools = ["echo"],
            Plugins = ["server"],
            McpInstallations = [new()
            {
                Id = "ws/server", PluginName = "server", DefinitionRevision = McpToolInstallation.ImplementationRevision,
                Url = "http://127.0.0.1:1234/mcp", ServerName = "echo", ServerVersion = "1", Operation = "echo",
                ConfigDigest = McpToolInstallation.ComputeConfigDigest("http://127.0.0.1:1234/mcp", "echo", "1", "echo"),
                ContractDigest = new string('a', 64)
            }]
        };

        public Fixture()
        {
            var actors = Substitute.For<IVirtualActorProvider>();
            actors.GetActor<IToolRegistryActor>(Arg.Any<VirtualActorId>()).Returns(Registry);
            Recovery = new(actors, Installations, Probe, Substitute.For<ICapabilityAuthorizer>());
            SetTools(Tool);
            Installations.MatchesInstallation(Arg.Any<McpToolInstallationSnapshot>()).Returns(true);
            Registry.RestoreMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        public void SetTools(McpToolRecoveryPlan tool) =>
            Registry.GetMcpRecoveryPlansAsync(Arg.Any<CancellationToken>()).Returns(new[] { tool });

        public async Task<string?> RestoreAsync() => await Recovery.RestoreAsync(Services,
            (await Recovery.DescribeAsync(Services, TestContext.Current.CancellationToken)).Digest, new(), TestContext.Current.CancellationToken);
    }

    private sealed class ProbeStub : IToolInstallationPeerProbe
    {
        public Func<McpToolInstallation, CancellationToken, Task<InstallationFailureCode>> OnProbe { get; set; } =
            (_, _) => Task.FromResult(InstallationFailureCode.None);
        public Task<InstallationFailureCode> ProbeAsync(McpToolInstallation installation, CancellationToken ct) => OnProbe(installation, ct);
        public Task<InstallationFailureCode> ProbeAsync(DaprToolInstallation installation, CancellationToken ct) =>
            throw new NotSupportedException("Unexpected Dapr probe in MCP restoration.");
    }
}
