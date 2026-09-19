using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Actors;
using Weave.Security.Events;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

public sealed class ToolOperationAuthorizationTests
{
    [Theory]
    [InlineData("read_file")]
    [InlineData("READ_FILE")]
    public async Task InvokeAsync_ExactReadGrant_ReturnsRealFileContents(string method)
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var result = await fx.Actor.InvokeAsync(fx.Read() with { Method = method }, fx.Token("tool:files:invoke:read_file"));
        result.Success.ShouldBeTrue(result.Error);
        result.Output.ShouldBe("original");
        fx.Decisions.ShouldContain(e => e.Grant == "tool:files:invoke:read_file"
            && e.Outcome == CapabilityAuthorizationOutcome.Allow);
    }

    [Theory]
    [InlineData("tool:files:invoke:read_file")]
    [InlineData("tool:files:connect")]
    [InlineData("tool:files")]
    [InlineData("tool:other:invoke:write_file")]
    public async Task InvokeAsync_NoWriteGrant_DoesNotChangeFile(string grant)
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.InvokeAsync(fx.Write(), fx.Token(grant)));
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData("tool:files:invoke:write_file")]
    [InlineData("tool:files:invoke:*")]
    [InlineData("tool:*")]
    public async Task InvokeAsync_ExplicitWriteAuthority_ChangesRealFile(string grant)
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var result = await fx.Actor.InvokeAsync(fx.Write(), fx.Token(grant));
        result.Success.ShouldBeTrue(result.Error);
        File.ReadAllText(fx.Target).ShouldBe("updated");
    }

    [Fact]
    public async Task ConnectAsync_ConnectionGrant_ConnectsWithoutInvocationAuthority()
    {
        using var fx = new Fixture();
        var token = fx.Token("tool:files:connect");
        var handle = await fx.Actor.ConnectAsync(fx.Spec, token);
        handle.IsConnected.ShouldBeTrue();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.InvokeAsync(fx.Write(), token));
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task InvokeAsync_ClaimedToolDiffersFromActor_RejectsWithoutDispatch()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.InvokeAsync(
            fx.Write() with { ToolName = "another-installation" }, fx.Token("tool:*")));
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task ConnectAsync_ClaimedToolDiffersFromActor_DoesNotReplaceConnection()
    {
        using var fx = new Fixture();
        var connected = await fx.ConnectAsync();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.ConnectAsync(
            fx.Spec with { Name = "another-installation" }, fx.Token("tool:*")));
        (await fx.Actor.GetHandleAsync()).ShouldBe(connected);
    }

    [Fact]
    public async Task InvokeAsync_TokenRevokedDuringSecretResolution_DoesNotDispatch()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var token = fx.Token("tool:*");
        fx.Proxy.SubstituteAsync(Arg.Any<string>()).Returns(ci =>
        {
            fx.Tokens.Revoke(token.TokenId);
            return ci.Arg<string>();
        });
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.InvokeAsync(fx.Write(), token));
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task InvokeAsync_CallerMutatesParametersDuringAwait_ExecutesCapturedInput()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        var invocation = fx.Write();
        fx.Proxy.SubstituteAsync(Arg.Any<string>()).Returns(ci =>
        {
            invocation.Parameters["path"] = "different.txt";
            return ci.Arg<string>();
        });
        var result = await fx.Actor.InvokeAsync(invocation, fx.Token("tool:*"));
        result.Success.ShouldBeTrue(result.Error);
        File.ReadAllText(fx.Target).ShouldBe("updated");
        File.Exists(Path.Combine(Path.GetDirectoryName(fx.Target)!, "different.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task InvokeAsync_CancelledBeforeDispatch_DoesNotChangeFile()
    {
        using var fx = new Fixture();
        await fx.ConnectAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var token = fx.Token("tool:*") with { CancellationToken = cancelled.Token };
        await Should.ThrowAsync<OperationCanceledException>(() => fx.Actor.InvokeAsync(fx.Write(), token));
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task InvokeAsync_ShellCallLabelledRead_DoesNotExecuteUnderReadGrant()
    {
        using var fx = new Fixture(cli: true);
        await fx.ConnectAsync();
        var invocation = new ToolInvocation
        {
            ToolName = "shell",
            Method = "read_file",
            RawInput = Fixture.ShellCommand
        };
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Actor.InvokeAsync(
            invocation, fx.Token("tool:shell:invoke:read_file")));
        fx.Decisions.ShouldNotContain(e => e.Grant == "tool:shell:invoke:exec"
            && e.Outcome == CapabilityAuthorizationOutcome.Allow);
    }

    [Fact]
    public async Task InvokeAsync_ExplicitShellExecGrant_ExecutesShellOperation()
    {
        using var fx = new Fixture(cli: true);
        await fx.ConnectAsync();
        var invocation = new ToolInvocation
        {
            ToolName = "shell",
            Method = "invoke",
            RawInput = Fixture.ShellCommand
        };
        var result = await fx.Actor.InvokeAsync(invocation, fx.Token("tool:shell:invoke:exec"));
        result.Success.ShouldBeTrue(result.Error);
        result.Output.TrimEnd().ShouldBe("harmless");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"weave-operation-{Guid.NewGuid():N}");
        private readonly IDisposable _subscription;
        public ISecretProxyActor Proxy { get; } = Substitute.For<ISecretProxyActor>();
        public CapabilityTokenService Tokens { get; }
        public ToolActor Actor { get; }
        public ToolSpec Spec { get; }
        public string Target => Path.Combine(_directory, "document.txt");
        public List<CapabilityAuthorizationEvent> Decisions { get; } = [];
        public static string ShellCommand => OperatingSystem.IsWindows() ? "Write-Output harmless" : "printf harmless";

        public Fixture(bool cli = false)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Target, "original");
            Tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
            {
                SigningKey = "test-signing-key-that-is-at-least-32-chars-long",
                RevocationDirectory = Path.Combine(_directory, "revocations")
            }), TimeProvider.System);
            var events = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
            _subscription = events.Subscribe<CapabilityAuthorizationEvent>((e, _) =>
            {
                Decisions.Add(e);
                return Task.CompletedTask;
            });
            var actors = Substitute.For<IVirtualActorProvider>();
            Proxy.SubstituteAsync(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
            actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(Proxy);
            IToolConnector connector = cli
                ? new CliToolConnector(NullLogger<CliToolConnector>.Instance)
                : new FileSystemToolConnector(NullLogger<FileSystemToolConnector>.Instance);
            var discovery = new ToolDiscoveryService([connector], NullLogger<ToolDiscoveryService>.Instance);
            Actor = new ToolActor(actors, discovery, new LeakScanner(NullLogger<LeakScanner>.Instance),
                new CapabilityAuthorizer(Tokens, events, NullLogger<CapabilityAuthorizer>.Instance),
                new LifecycleManager(NullLogger<LifecycleManager>.Instance), events, NullLogger<ToolActor>.Instance);
            Spec = new ToolSpec
            {
                Name = cli ? "shell" : "files",
                Type = connector.ToolType,
                FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = _directory },
                Cli = new CliConfig
                {
                    Shell = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh",
                    AllowedCommands = ["printf *", "Write-Output *"]
                }
            };
        }

        public CapabilityToken Token(params string[] grants) => Tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "workspace-a",
            IssuedTo = "workspace-a/reader",
            Grants = [.. grants],
            Lifetime = TimeSpan.FromMinutes(5)
        });

        public Task<ToolHandle> ConnectAsync() => Actor.ConnectAsync(Spec, Token("tool:*"));
        public ToolInvocation Read() => new()
        {
            ToolName = Spec.Name,
            Method = "read_file",
            Parameters = new() { ["path"] = "document.txt" }
        };
        public ToolInvocation Write() => new()
        {
            ToolName = Spec.Name,
            Method = "write_file",
            Parameters = new() { ["path"] = "document.txt" },
            RawInput = "updated"
        };

        public void Dispose()
        {
            _subscription.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
