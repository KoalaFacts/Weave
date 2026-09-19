using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Invocations;
using Weave.Security.Actors;
using Weave.Security.Scanning;
using Weave.Security.Sqlite;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Shared.VirtualActors;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class ApprovalAdmissionRegressionTests
{
    [Fact]
    public async Task InvokeAsync_UppercaseWriteOperation_StillRequiresApproval()
    {
        using var fx = new Fixture();
        var actor = await fx.ConnectAsync();
        var request = fx.Write() with { Method = "WRITE_FILE" };

        var result = await actor.InvokeAsync(request, fx.Token());

        AssertPending(result, request);
        fx.AssertUnchanged();
        fx.Journal.Find("workspace", request.InvocationId!.Value, TestContext.Current.CancellationToken).ShouldBeNull();
    }

    [Theory]
    [InlineData("body")]
    [InlineData("path")]
    [InlineData("operation")]
    [InlineData("subject")]
    public async Task InvokeAsync_PendingIdWithChangedPlan_RejectsWithoutDispatch(string change)
    {
        using var fx = new Fixture();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();
        AssertPending(await actor.InvokeAsync(request, fx.Token()), request);
        fx.AssertUnchanged();
        var changed = change switch
        {
            "body" => request with { RawInput = "unapproved replacement" },
            "path" => request with { Parameters = new() { ["path"] = "different.txt" } },
            "operation" => request with { Method = "read_file", RawInput = null },
            _ => request
        };

        var result = await actor.InvokeAsync(changed, fx.Token(change == "subject" ? "other-writer" : "writer"));

        AssertConflict(result);
        fx.AssertUnchanged();
        fx.Journal.Find("workspace", request.InvocationId!.Value, TestContext.Current.CancellationToken).ShouldBeNull();
        // A conflicting submission must not replace the first pending request.
        AssertPending(await actor.InvokeAsync(request, fx.Token()), request);
        fx.AssertUnchanged();
    }

    [Fact]
    public async Task InvokeAsync_PendingIdAfterFilesystemRootChange_RejectsNewTarget()
    {
        using var fx = new Fixture();
        var request = fx.Write();
        var actor = await fx.ConnectAsync();
        AssertPending(await actor.InvokeAsync(request, fx.Token()), request);
        fx.AssertUnchanged();
        var other = await fx.ConnectAsync(fx.Reopen(), alternateTarget: true);

        var result = await other.InvokeAsync(request, fx.Token());

        AssertConflict(result);
        fx.AssertUnchanged();
        fx.Journal.Find("workspace", request.InvocationId!.Value, TestContext.Current.CancellationToken).ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_PendingIdAfterApprovalPolicyRemoval_DoesNotBypassStoredRequirement()
    {
        using var fx = new Fixture();
        var request = fx.Write();
        var actor = await fx.ConnectAsync();
        AssertPending(await actor.InvokeAsync(request, fx.Token()), request);
        fx.AssertUnchanged();
        var reopened = await fx.ConnectAsync(fx.Reopen(requireApproval: false));

        var result = await reopened.InvokeAsync(request, fx.Token());

        result.Success.ShouldBeFalse();
        result.AttemptId.ShouldBeNull();
        result.ErrorCode.ShouldNotBeNull();
        fx.AssertUnchanged();
        fx.Journal.Find("workspace", request.InvocationId!.Value, TestContext.Current.CancellationToken).ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_SeparateReadWhileWritePending_ReturnsReadResultOnly()
    {
        using var fx = new Fixture();
        var actor = await fx.ConnectAsync();
        var write = fx.Write();
        AssertPending(await actor.InvokeAsync(write, fx.Token()), write);
        var read = fx.Write() with { Method = "read_file", RawInput = null };

        var result = await actor.InvokeAsync(read, fx.Token());

        result.Success.ShouldBeTrue(result.Error);
        result.Output.ShouldBe("original");
        result.InvocationId.ShouldBe(read.InvocationId);
        result.AttemptId.ShouldNotBeNull();
        fx.AssertUnchanged();
        AssertPending(await actor.InvokeAsync(write, fx.Token()), write);
    }

    private static void AssertPending(ToolResult result, ToolInvocation request)
    {
        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("approval-pending");
        result.InvocationId.ShouldBe(request.InvocationId);
        result.AttemptId.ShouldBeNull();
    }

    private static void AssertConflict(ToolResult result)
    {
        result.Success.ShouldBeFalse();
        result.AttemptId.ShouldBeNull();
        result.ErrorCode.ShouldNotBeNull().ShouldNotBe("approval-pending");
        result.Output.ShouldBeEmpty();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-approval-admission-{Guid.NewGuid():N}");
        private readonly CapabilityTokenService _tokens;
        private string FirstRoot => Path.Combine(_root, "first");
        private string SecondRoot => Path.Combine(_root, "second");
        public SqliteInvocationJournal Journal { get; }

        public Fixture()
        {
            Directory.CreateDirectory(FirstRoot);
            Directory.CreateDirectory(SecondRoot);
            File.WriteAllText(Path.Combine(FirstRoot, "document.txt"), "original");
            File.WriteAllText(Path.Combine(SecondRoot, "document.txt"), "other target");
            _tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
            {
                SigningKey = "test-signing-key-that-is-at-least-32-chars-long",
                RevocationDirectory = Path.Combine(_root, "revocations")
            }), TimeProvider.System);
            Journal = Reopen();
        }

        public SqliteInvocationJournal Reopen(bool requireApproval = true)
        {
            string[] required = requireApproval ? ["tool:files:invoke:write_file"] : [];
            // Keep the test executable against the pre-implementation contract.
            var options = JsonSerializer.Deserialize<InvocationJournalOptions>(JsonSerializer.Serialize(new
            {
                DatabasePath = Path.Combine(_root, "journal.db"),
                ApprovalRequiredGrants = required,
                ApprovalLifetime = "00:05:00"
            }))!;
            return new SqliteInvocationJournal(Options.Create(options));
        }

        public async Task<ToolActor> ConnectAsync(IInvocationJournal? journal = null, bool alternateTarget = false)
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
                Name = "files",
                Type = ToolType.FileSystem,
                FileSystem = new FileSystemToolConfig { Root = alternateTarget ? SecondRoot : FirstRoot }
            }, Token());
            return actor;
        }

        public CapabilityToken Token(string subject = "writer") => _tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "workspace",
            IssuedTo = subject,
            Grants = ["tool:files:connect", "tool:files:invoke:read_file", "tool:files:invoke:write_file"],
            Lifetime = TimeSpan.FromMinutes(5)
        });

        public ToolInvocation Write() => new()
        {
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
            ToolName = "files",
            Method = "write_file",
            Parameters = new() { ["path"] = "document.txt" },
            RawInput = "reviewed change"
        };

        public void AssertUnchanged()
        {
            File.ReadAllText(Path.Combine(FirstRoot, "document.txt")).ShouldBe("original");
            File.ReadAllText(Path.Combine(SecondRoot, "document.txt")).ShouldBe("other target");
            File.Exists(Path.Combine(FirstRoot, "different.txt")).ShouldBeFalse();
            File.Exists(Path.Combine(SecondRoot, "different.txt")).ShouldBeFalse();
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
