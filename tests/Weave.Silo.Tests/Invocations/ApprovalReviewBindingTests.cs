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

public sealed class ApprovalReviewBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewApprovalAsync_SubstitutedInput_NeverResolvesSecretsForReviewer(bool substituted)
    {
        var root = Path.Combine(Path.GetTempPath(), $"weave-review-binding-{Guid.NewGuid():N}");
        var toolRoot = Path.Combine(root, "tools");
        Directory.CreateDirectory(toolRoot);
        var target = Path.Combine(toolRoot, "note.txt");
        File.WriteAllText(target, "original");
        try
        {
            var tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
            {
                SigningKey = "test-review-" + Guid.NewGuid().ToString("N"),
                RevocationDirectory = Path.Combine(root, "revocations")
            }), TimeProvider.System);
            CapabilityToken Token(string subject, HashSet<string> grants) => tokens.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "workspace",
                IssuedTo = subject,
                Grants = grants,
                Lifetime = TimeSpan.FromMinutes(5)
            });
            var events = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
            var proxy = Substitute.For<ISecretProxyActor>();
            proxy.SubstituteAsync(Arg.Any<string>()).Returns(call => call.Arg<string>());
            proxy.SubstituteAsync("${secrets.note_path}").Returns("note.txt");
            var actors = Substitute.For<IVirtualActorProvider>();
            actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(proxy);
            var connector = new FileSystemToolConnector(NullLogger<FileSystemToolConnector>.Instance);
            var journal = new SqliteInvocationJournal(Options.Create(new InvocationJournalOptions
            {
                DatabasePath = Path.Combine(root, "journal.db"),
                ApprovalRequiredGrants = ["tool:files:invoke:write_file"]
            }));
            var actor = new ToolActor(actors,
                new ToolDiscoveryService([connector], NullLogger<ToolDiscoveryService>.Instance),
                new LeakScanner(NullLogger<LeakScanner>.Instance),
                new CapabilityAuthorizer(tokens, events, NullLogger<CapabilityAuthorizer>.Instance),
                new LifecycleManager(NullLogger<LifecycleManager>.Instance), events,
                NullLogger<ToolActor>.Instance, journal, TimeProvider.System);
            await actor.ConnectAsync(new ToolSpec
            {
                Name = "files",
                Type = ToolType.FileSystem,
                FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = toolRoot }
            }, Token("setup", ["tool:files:connect"]));
            var request = new ToolInvocation
            {
                InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
                ToolName = "files",
                Method = "write_file",
                Parameters = new() { ["path"] = substituted ? "${secrets.note_path}" : "note.txt" },
                RawInput = "reviewed"
            };
            var pending = await actor.InvokeAsync(request, Token("writer", ["tool:files:invoke:write_file"]));
            pending.ErrorCode.ShouldBe("approval-pending");
            proxy.ClearReceivedCalls();
            var reviewer = Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);

            var result = await actor.ReviewApprovalAsync(request, reviewer);

            await proxy.DidNotReceive().SubstituteAsync(Arg.Any<string>());
            var approval = (await actor.GetApprovalAsync(request.InvocationId.Value, reviewer)).ShouldNotBeNull();
            approval.State.ShouldBe(InvocationApprovalState.Pending);
            if (substituted)
            {
                result.Review.ShouldBeNull();
                result.ErrorCode.ShouldBe("approval-review-unavailable");
            }
            else
            {
                result.ErrorCode.ShouldBeNull();
                var review = result.Review.ShouldNotBeNull();
                review.Parameters["path"].ShouldBe("note.txt");
                review.RawInput.ShouldBe("reviewed");
                // Golden v1 encoding: extraction for review must not change existing persisted fingerprints.
                approval.InputDigest.ShouldBe("v1:81091E49FE7A10FA424CA36672DA9E4E42C1BD4B1FFA83AAF4E1EA0A5DDF31F2");
                request.Parameters["path"] = "mutated-after-review.txt";
                review.Parameters["path"].ShouldBe("note.txt");
            }
            journal.Find("workspace", request.InvocationId.Value, TestContext.Current.CancellationToken).ShouldBeNull();
            File.ReadAllText(target).ShouldBe("original");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
