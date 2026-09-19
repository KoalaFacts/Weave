using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Invocations;
using Weave.Security.Actors;
using Weave.Security.Scanning;
using Weave.Security.Sqlite;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Shared.VirtualActors;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class ToolApprovalBoundaryTests
{
    [Fact]
    public async Task InvokeAsync_RequiredApproval_DoesNotWriteOrStartAnAttempt()
    {
        using var fx = new Fixture();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();

        var result = await actor.InvokeAsync(request, fx.Token("tool:files:invoke:write_file"));

        File.ReadAllText(fx.Target).ShouldBe("original");
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("approval-pending");
        result.InvocationId.ShouldBe(request.InvocationId);
        result.AttemptId.ShouldBeNull();
        fx.Journal.Find("workspace", request.InvocationId!.Value, TestContext.Current.CancellationToken).ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_PendingAfterReopen_RemainsBlockedWithoutReplay()
    {
        using var fx = new Fixture();
        var request = fx.Write();
        var token = fx.Token("tool:files:invoke:write_file");
        await (await fx.ConnectAsync()).InvokeAsync(request, token);
        File.WriteAllText(fx.Target, "external change");

        var reopened = await fx.ConnectAsync(fx.Reopen());
        var result = await reopened.InvokeAsync(request, token);

        File.ReadAllText(fx.Target).ShouldBe("external change");
        result.ErrorCode.ShouldBe("approval-pending");
        result.AttemptId.ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_ReadNotRequiringApproval_ExecutesNormally()
    {
        using var fx = new Fixture();
        var actor = await fx.ConnectAsync();
        var result = await actor.InvokeAsync(fx.Write() with { Method = "read_file", RawInput = null },
            fx.Token("tool:files:invoke:read_file"));
        result.Success.ShouldBeTrue(result.Error);
        result.Output.ShouldBe("original");
    }

    [Fact]
    public async Task InvokeAsync_WithoutWriteAuthority_CannotRequestApprovalToAcquireIt()
    {
        using var fx = new Fixture();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.InvokeAsync(request,
            fx.Token("tool:files:invoke:read_file")));
        File.ReadAllText(fx.Target).ShouldBe("original");
        fx.Journal.Find("workspace", request.InvocationId!.Value, TestContext.Current.CancellationToken).ShouldBeNull();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] RequiredGrants = ["tool:files:invoke:write_file"];
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-approval-{Guid.NewGuid():N}");
        private readonly CapabilityTokenService _tokens = new(Options.Create(new CapabilityTokenOptions
        {
            SigningKey = "test-signing-key-that-is-at-least-32-chars-long"
        }), TimeProvider.System);
        public string Target => Path.Combine(_root, "document.txt");
        public SqliteInvocationJournal Journal { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Target, "original");
            Journal = Reopen();
        }

        public SqliteInvocationJournal Reopen()
        {
            // The baseline ignores this additive configuration field, exposing missing governance.
            var options = JsonSerializer.Deserialize<InvocationJournalOptions>(JsonSerializer.Serialize(new
            {
                DatabasePath = Path.Combine(_root, "journal.db"),
                ApprovalRequiredGrants = RequiredGrants,
                ApprovalLifetime = "00:05:00"
            }))!;
            return new SqliteInvocationJournal(Options.Create(options));
        }

        public async Task<ToolActor> ConnectAsync(IInvocationJournal? journal = null)
        {
            var actors = Substitute.For<IVirtualActorProvider>();
            var secrets = Substitute.For<ISecretProxyActor>();
            secrets.SubstituteAsync(Arg.Any<string>()).Returns(c => c.Arg<string>());
            actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(secrets);
            var events = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
            var connector = new FileSystemToolConnector(NullLogger<FileSystemToolConnector>.Instance);
            var actor = new ToolActor(actors,
                new ToolDiscoveryService([connector], NullLogger<ToolDiscoveryService>.Instance),
                new LeakScanner(NullLogger<LeakScanner>.Instance),
                new CapabilityAuthorizer(_tokens, events, NullLogger<CapabilityAuthorizer>.Instance),
                new LifecycleManager(NullLogger<LifecycleManager>.Instance), events,
                NullLogger<ToolActor>.Instance, journal ?? Journal, TimeProvider.System);
            await actor.OnActivatedAsync("workspace/files", TestContext.Current.CancellationToken);
            await actor.ConnectAsync(new ToolSpec
            {
                Name = "files", Type = ToolType.FileSystem,
                FileSystem = new FileSystemToolConfig { Root = _root }
            }, Token("tool:files:connect"));
            return actor;
        }

        public CapabilityToken Token(string grant) => _tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "workspace", IssuedTo = "writer", Grants = [grant], Lifetime = TimeSpan.FromHours(1)
        });

        public ToolInvocation Write() => new()
        {
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
            ToolName = "files", Method = "write_file",
            Parameters = new() { ["path"] = Path.GetFileName(Target) }, RawInput = "updated"
        };

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
