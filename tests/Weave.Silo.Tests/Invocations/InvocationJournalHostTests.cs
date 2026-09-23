using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class InvocationJournalHostTests
{
    [Fact]
    public async Task GetInvocationAsync_NewHostSameDatabase_ReturnsOutcomeWithoutReconnectingTool()
    {
        var root = Path.Combine(Path.GetTempPath(), $"weave-invocation-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "journal.db");
        var workspace = "ws-" + Guid.NewGuid().ToString("N");
        var id = InvocationId.From(Guid.NewGuid().ToString("N"));
        var request = Request(id);
        try
        {
            await using (var parent = new SiloFactory())
            await using (var first = Configure(parent, database))
            {
                using var client = first.CreateClient();
                var tool = Tool(first, workspace);
                var writer = Token(first, workspace, "writer", ["tool:files:connect", "tool:files:invoke:write_file"]);
                await tool.ConnectAsync(Spec(root), writer);
                var result = await tool.InvokeAsync(request, writer);
                result.Success.ShouldBeTrue(result.Error);
                result.InvocationId.ShouldBe(id);
                result.AttemptId.ShouldNotBeNull();
                result.OutcomeRecorded.ShouldBeTrue();
                File.ReadAllText(Path.Combine(root, "note.txt")).ShouldBe("private request marker");
            }

            // No original Host, service provider or actor activation remains alive.
            await using (var parent = new SiloFactory())
            await using (var second = Configure(parent, database))
            {
                using var client = second.CreateClient();
                var tool = Tool(second, workspace);
                (await tool.GetHandleAsync()).ShouldBeNull();
                var reader = Token(second, workspace, "writer", ["invocation:read"]);
                var record = (await tool.GetInvocationAsync(id, reader)).ShouldNotBeNull();
                record.InvocationId.ShouldBe(id);
                record.Attempt.Outcome.ShouldBe(InvocationOutcome.Succeeded);
                record.AuthorizedGrant.ShouldBe("tool:files:invoke:write_file");
                record.Subject.ShouldBe("writer");
                var metadata = JsonSerializer.Serialize(record);
                metadata.ShouldNotContain("private request marker");
                metadata.ShouldNotContain(reader.Signature);

                var writer = Token(second, workspace, "writer", ["tool:files:connect", "tool:files:invoke:write_file"]);
                await tool.ConnectAsync(Spec(root), writer);
                File.WriteAllText(Path.Combine(root, "note.txt"), "changed after host restart");
                var replay = await tool.InvokeAsync(request, writer);
                replay.IsReplay.ShouldBeTrue();
                replay.Success.ShouldBeTrue();
                replay.Output.ShouldBeEmpty();
                File.ReadAllText(Path.Combine(root, "note.txt")).ShouldBe("changed after host restart");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing-grant")]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("other-workspace")]
    [InlineData("other-subject")]
    [InlineData("other-tool")]
    public async Task GetInvocationAsync_UnauthorizedContext_DoesNotDiscloseRecord(string condition)
    {
        var root = Path.Combine(Path.GetTempPath(), $"weave-invocation-query-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var parent = new SiloFactory();
            await using var host = Configure(parent, Path.Combine(root, "journal.db"));
            using var client = host.CreateClient();
            var workspace = "ws-" + Guid.NewGuid().ToString("N");
            var tool = Tool(host, workspace);
            var writer = Token(host, workspace, "writer", ["tool:files:connect", "tool:files:invoke:write_file"]);
            await tool.ConnectAsync(Spec(root), writer);
            var id = InvocationId.From(Guid.NewGuid().ToString("N"));
            (await tool.InvokeAsync(Request(id), writer)).Success.ShouldBeTrue();
            var reader = Token(host,
                condition == "other-workspace" ? "foreign" : workspace,
                condition == "other-subject" ? "someone-else" : "writer",
                condition == "missing-grant" ? ["tool:files:invoke:write_file"] : ["invocation:read"],
                expired: condition == "expired");
            if (condition == "tampered")
                reader = reader with { Signature = "invalid" };
            if (condition == "revoked")
                host.Services.GetRequiredService<ICapabilityTokenService>().Revoke(reader.TokenId);
            if (condition == "other-tool")
                tool = host.Services.GetRequiredService<IVirtualActorProvider>()
                    .GetActor<IToolActor>(VirtualActorId.From(workspace + "/another-tool"));

            if (condition is "other-subject" or "other-tool")
                (await tool.GetInvocationAsync(id, reader)).ShouldBeNull();
            else
                await Should.ThrowAsync<UnauthorizedAccessException>(() => tool.GetInvocationAsync(id, reader));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_RealJournalRejectsAttempt_DoesNotWriteThroughActor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"weave-invocation-block-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "journal.db");
        try
        {
            await using var parent = new SiloFactory();
            await using var host = Configure(parent, database);
            using var client = host.CreateClient();
            var workspace = "ws-" + Guid.NewGuid().ToString("N");
            var tool = Tool(host, workspace);
            var token = Token(host, workspace, "writer", ["tool:files:connect", "tool:files:invoke:write_file"]);
            await tool.ConnectAsync(Spec(root), token);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER no_attempt BEFORE INSERT ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'unavailable'); END;";
                command.ExecuteNonQuery();
            }
            var result = await tool.InvokeAsync(Request(InvocationId.From(Guid.NewGuid().ToString("N"))), token);
            result.Success.ShouldBeFalse();
            result.ErrorCode.ShouldBe("journal-write-failed");
            File.Exists(Path.Combine(root, "note.txt")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_MemoryJournalPath_RejectsHostStartup()
    {
        await using var parent = new SiloFactory();
        await using var host = Configure(parent, ":memory:");
        Should.Throw<InvalidOperationException>(() => host.CreateClient());
    }

    private static WebApplicationFactory<Program> Configure(SiloFactory parent, string database) =>
        parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<InvocationJournalOptions>(options => options.DatabasePath = database)));

    private static IToolActor Tool(WebApplicationFactory<Program> host, string workspace) =>
        host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IToolActor>(VirtualActorId.From(workspace + "/files"));

    private static CapabilityToken Token(WebApplicationFactory<Program> host, string workspace, string subject,
        HashSet<string> grants, bool expired = false) =>
        host.Services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspace,
            IssuedTo = subject,
            Grants = grants,
            Lifetime = expired ? TimeSpan.FromMinutes(-1) : TimeSpan.FromMinutes(5)
        });

    private static ToolSpec Spec(string root) => new()
    {
        Name = "files",
        Type = ToolType.FileSystem,
        FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = root }
    };

    private static ToolInvocation Request(InvocationId id) => new()
    {
        InvocationId = id,
        ToolName = "files",
        Method = "write_file",
        Parameters = new() { ["path"] = "note.txt" },
        RawInput = "private request marker"
    };
}
