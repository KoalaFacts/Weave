using Microsoft.Data.Sqlite;
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

internal sealed class ApprovalScenario : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"weave-approval-flow-{Guid.NewGuid():N}");
    public string Target => Path.Combine(Root, "document.txt");
    public string DatabasePath => Path.Combine(Root, "journal.db");
    public ApprovalClock Clock { get; } = new();
    public CapabilityTokenService Tokens { get; }
    public ISecretProxyActor Secrets { get; } = Substitute.For<ISecretProxyActor>();
    public SqliteInvocationJournal Journal { get; }

    public ApprovalScenario()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Target, "original");
        Tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
        {
            SigningKey = "test-signing-key-that-is-at-least-32-chars-long",
            RevocationDirectory = Path.Combine(Root, "revocations")
        }), Clock);
        Secrets.SubstituteAsync(Arg.Any<string>()).Returns(c => c.Arg<string>());
        Journal = Reopen();
    }

    public SqliteInvocationJournal Reopen(bool requireApproval = true) => new(Options.Create(new InvocationJournalOptions
    {
        DatabasePath = DatabasePath,
        ApprovalRequiredGrants = requireApproval ? ["tool:files:invoke:write_file"] : [],
        ApprovalLifetime = TimeSpan.FromMinutes(5)
    }));

    public async Task<ToolActor> ConnectAsync(IInvocationJournal? journal = null, string? root = null)
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(Secrets);
        var events = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var connector = new FileSystemToolConnector(NullLogger<FileSystemToolConnector>.Instance);
        var actor = new ToolActor(actors,
            new ToolDiscoveryService([connector], NullLogger<ToolDiscoveryService>.Instance),
            new LeakScanner(NullLogger<LeakScanner>.Instance),
            new CapabilityAuthorizer(Tokens, events, NullLogger<CapabilityAuthorizer>.Instance),
            new LifecycleManager(NullLogger<LifecycleManager>.Instance), events,
            NullLogger<ToolActor>.Instance, journal ?? Journal, Clock);
        await actor.OnActivatedAsync("workspace/files", TestContext.Current.CancellationToken);
        await actor.ConnectAsync(new ToolSpec
        {
            Name = "files",
            Type = ToolType.FileSystem,
            FileSystem = new FileSystemToolConfig { Root = root ?? Root }
        }, Token("operator", "tool:files:connect"));
        return actor;
    }

    public CapabilityToken Token(string subject, params string[] grants) => Tokens.Mint(new CapabilityTokenRequest
    {
        WorkspaceId = "workspace",
        IssuedTo = subject,
        Grants = [.. grants],
        Lifetime = TimeSpan.FromHours(1)
    });

    public CapabilityToken Approver() => Token("approver", "approval:decide", "tool:files:approve:write_file", "invocation:read");
    public CapabilityToken Writer() => Token("writer", "tool:files:invoke:write_file", "invocation:read", "invocation:cancel");

    public ToolInvocation Write() => new()
    {
        InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
        ToolName = "files",
        Method = "write_file",
        Parameters = new() { ["path"] = Path.GetFileName(Target) },
        RawInput = "updated"
    };

    public async Task<InvocationApproval> WaitAsync(ToolActor actor, ToolInvocation request)
    {
        var result = await actor.InvokeAsync(request, Writer());
        result.ErrorCode.ShouldBe("approval-pending");
        result.AttemptId.ShouldBeNull();
        return (await actor.GetApprovalAsync(request.InvocationId!.Value, Writer())).ShouldNotBeNull();
    }

    public void ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public long Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
