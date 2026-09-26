using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class UuidProposalRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weave-uuid-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private const string Proposed = "Frozen proposal 汉🙂\nSecond line.\n";

    [Fact]
    public async Task Resume_AfterHostAndClientBodyLoss_ReviewsByUuidAndClaimsOneAttempt()
    {
        string id;
        await using (var first = new Fixture(_root, _clock))
        {
            await first.ConnectAsync();
            var request = Request();
            id = request.InvocationId!.Value.ToString();
            using var pending = await first.Send(HttpMethod.Post, "", request, first.Agent());
            pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            first.Text.ShouldBe("original");
        }
        // No request object/body file is carried into either fresh Host/client below.
        await using (var second = new Fixture(_root, _clock, recovery: true))
        {
            using var proposal = await second.Send(HttpMethod.Get, "/" + id + "/proposal", null, second.Reader());
            proposal.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await Json(proposal)).GetProperty("rawInput").GetString().ShouldBe(Proposed);
            await second.ConnectAsync();
            var digest = await Review(second, id);
            using var decision = await second.Send(HttpMethod.Post, "/" + id + "/decision",
                new { decision = "approve", planDigest = digest }, second.Reviewer());
            decision.StatusCode.ShouldBe(HttpStatusCode.OK);
            second.Text.ShouldBe("original");
            second.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
            using var denied = await second.Send(HttpMethod.Post, "/" + id + "/resume", null, second.Reader());
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            second.Text.ShouldBe("original");

            var replies = await Task.WhenAll(second.Send(HttpMethod.Post, "/" + id + "/resume", null, second.Agent()),
                second.Send(HttpMethod.Post, "/" + id + "/resume", null, second.Agent()));
            var replays = 0;
            foreach (var reply in replies)
            {
                using (reply)
                {
                    reply.StatusCode.ShouldBe(HttpStatusCode.OK);
                    var result = await Json(reply);
                    result.GetProperty("outcome").GetString().ShouldBe("Succeeded");
                    if (result.GetProperty("isReplay").GetBoolean())
                        replays++;
                }
            }
            replays.ShouldBe(1);
            second.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(1);
            second.Text.ShouldBe(Proposed);
            File.WriteAllText(second.Target, "later independent sentinel");
        }
        await using (var third = new Fixture(_root, _clock, recovery: true))
        {
            await third.ConnectAsync();
            using var repeated = await third.Send(HttpMethod.Post, "/" + id + "/resume", null, third.Agent());
            repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await Json(repeated)).GetProperty("isReplay").GetBoolean().ShouldBeTrue();
            third.Text.ShouldBe("later independent sentinel");
            third.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(1);
        }
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("expire")]
    [InlineData("revoke")]
    [InlineData("changed-digest")]
    [InlineData("changed-target")]
    public async Task DecideOrResume_InvalidCurrentPlanOrAuthority_DoesNotExecute(string condition)
    {
        await using var fx = new Fixture(_root, _clock);
        await fx.ConnectAsync();
        var id = await Pending(fx);
        var digest = await Review(fx, id);
        if (condition == "expire")
            _clock.Advance(TimeSpan.FromMinutes(6));
        if (condition == "changed-target")
            await fx.ConnectAsync(Path.Combine(_root, "another-target"));
        using var decision = await fx.Send(HttpMethod.Post, "/" + id + "/decision",
            new { decision = condition == "reject" ? "reject" : "approve", planDigest = condition == "changed-digest" ? "approval-v1:" + new string('0', 64) : digest },
            fx.Reviewer());
        decision.StatusCode.ShouldBe(condition is "reject" or "revoke" ? HttpStatusCode.OK : HttpStatusCode.Conflict);
        fx.Text.ShouldBe("original");
        var agent = fx.Agent();
        if (condition == "revoke")
            fx.Tokens.Revoke(agent.TokenId);
        using var resumed = await fx.Send(HttpMethod.Post, "/" + id + "/resume", null, agent);
        resumed.IsSuccessStatusCode.ShouldBe(condition == "changed-digest");
        if (condition == "changed-digest")
            resumed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fx.Text.ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Theory]
    [InlineData("/proposal", "GET")]
    [InlineData("/resume", "POST")]
    [InlineData("/approval/review", "GET")]
    public async Task UuidEndpoint_SuppliedBodyOrQuery_IsNotAnAlternateProposal(string suffix, string method)
    {
        await using var fx = new Fixture(_root, _clock);
        await fx.ConnectAsync();
        var id = await Pending(fx);
        var token = suffix == "/approval/review" ? fx.Reviewer() : fx.Reader();
        using var response = await fx.Send(new HttpMethod(method), "/" + id + suffix,
            new { rawInput = "replacement-private-marker" }, token);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var queried = await fx.Send(new HttpMethod(method), "/" + id + suffix + "?path=other", null, token);
        queried.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        fx.Text.ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Fact]
    public async Task Persist_ProposalInsertFails_RollsBackApprovalAndNeverDispatches()
    {
        await using var fx = new Fixture(_root, _clock);
        await fx.ConnectAsync();
        fx.Execute("CREATE TRIGGER reject_proposal BEFORE INSERT ON invocation_proposals BEGIN SELECT RAISE(ABORT, 'isolated test write fault'); END;");
        using var response = await fx.Send(HttpMethod.Post, "", Request(), fx.Agent());
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Json(response)).GetProperty("errorCode").GetString().ShouldBe("journal-write-failed");
        fx.Scalar("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(0);
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
        fx.Scalar("SELECT COUNT(*) FROM invocation_proposals;").ShouldBe(0);
        fx.Text.ShouldBe("original");
        fx.Execute("DROP TRIGGER reject_proposal;");
        using var healthy = await fx.Send(HttpMethod.Post, "", Request(), fx.Agent());
        healthy.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fx.Scalar("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(1);
        fx.Scalar("SELECT COUNT(*) FROM invocation_proposals;").ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredProposal_MissingOrCorrupt_FailsWithoutAcceptingAReplacement(bool corrupt)
    {
        await using var fx = new Fixture(_root, _clock);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.Send(HttpMethod.Post, "", request, fx.Agent());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = request.InvocationId!.Value.ToString();
        fx.Execute(corrupt ? "UPDATE invocation_proposals SET request_json=x'7B7D';" : "DELETE FROM invocation_proposals;");
        var expected = corrupt ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Conflict;
        using var read = await fx.Send(HttpMethod.Get, "/" + id + "/proposal", null, fx.Reader());
        read.StatusCode.ShouldBe(expected);
        using var review = await fx.Send(HttpMethod.Get, "/" + id + "/approval/review", null, fx.Reviewer());
        review.StatusCode.ShouldBe(expected);
        using var resume = await fx.Send(HttpMethod.Post, "/" + id + "/resume", null, fx.Agent());
        resume.StatusCode.ShouldBe(expected);
        using var backfill = await fx.Send(HttpMethod.Post, "", request, fx.Agent());
        backfill.StatusCode.ShouldBe(expected);
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
        fx.Scalar("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(1);
        fx.Text.ShouldBe("original");
    }

    [Fact]
    public async Task Restart_PreProposalDatabase_UpgradesWithoutInventingHistoricalBodies()
    {
        string id;
        await using (var first = new Fixture(_root, _clock))
        {
            await first.ConnectAsync();
            id = await Pending(first);
            // Test-owned database models the previous four-table/version-zero schema.
            first.Execute("DROP TABLE invocation_proposals; PRAGMA user_version=0;");
        }
        await using var restored = new Fixture(_root, _clock, recovery: true);
        restored.Scalar("PRAGMA user_version;").ShouldBe(1);
        restored.Scalar("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(1);
        restored.Scalar("SELECT COUNT(*) FROM invocation_proposals;").ShouldBe(0);
        using var proposal = await restored.Send(HttpMethod.Get, "/" + id + "/proposal", null, restored.Reader());
        proposal.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Json(proposal)).GetProperty("errorCode").GetString().ShouldBe("proposal-unavailable");
        await restored.ConnectAsync();
        using var fresh = await restored.Send(HttpMethod.Post, "", Request(), restored.Agent());
        fresh.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        restored.Scalar("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(2);
        restored.Scalar("SELECT COUNT(*) FROM invocation_proposals;").ShouldBe(1);
    }

    [Fact]
    public async Task Restart_VersionedProposalTableLost_DoesNotRecreateEmptyEvidence()
    {
        await using (var first = new Fixture(_root, _clock))
        {
            await first.ConnectAsync();
            _ = await Pending(first);
            first.Execute("DROP TABLE invocation_proposals;");
        }
        // A retained version-one store is not mistaken for an old schema upgrade.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "journal.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        var options = Microsoft.Extensions.Options.Options.Create(new InvocationJournalOptions
        {
            DatabasePath = Path.Combine(_root, "journal.db"),
            RequireExistingStorage = true
        });
        Should.Throw<SqliteException>(() => new Weave.Security.Sqlite.SqliteInvocationJournal(options));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='invocation_proposals';";
        Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
    }

    private static async Task<string> Pending(Fixture fx)
    {
        var request = Request();
        using var result = await fx.Send(HttpMethod.Post, "", request, fx.Agent());
        result.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return request.InvocationId!.Value.ToString();
    }

    private static async Task<string> Review(Fixture fx, string id)
    {
        using var review = await fx.Send(HttpMethod.Get, "/" + id + "/approval/review", null, fx.Reviewer());
        review.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Json(review);
        body.GetProperty("rawInput").GetString().ShouldBe(Proposed);
        return body.GetProperty("planDigest").GetString().ShouldNotBeNull();
    }

    private static ToolInvocation Request() => new()
    {
        InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
        ToolName = "files",
        Method = "write_file",
        Parameters = new() { ["path"] = "note.txt" },
        RawInput = Proposed
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class Clock : TimeProvider
    {
        private TimeSpan _offset;
        // Orleans also consumes this registration; start aligned with its wall clock.
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + _offset;
        public void Advance(TimeSpan amount) => _offset += amount;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SiloFactory _parent = new();
        private readonly WebApplicationFactory<Program> _host;
        private readonly string _root;
        public HttpClient Client { get; }
        public string Target => Path.Combine(_root, "tools", "note.txt");
        public string Text => File.ReadAllText(Target);
        public ICapabilityTokenService Tokens => _host.Services.GetRequiredService<ICapabilityTokenService>();
        private IToolActor Tool => _host.Services.GetRequiredService<IVirtualActorProvider>().GetActor<IToolActor>(VirtualActorId.From("uuid-pilot/files"));

        public Fixture(string root, TimeProvider clock, bool recovery = false, bool requireApproval = true)
        {
            _root = root;
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            if (!File.Exists(Target))
                File.WriteAllText(Target, "original");
            _host = _parent.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Weave:Auth:Mode", "none");
                builder.UseSetting("Weave:Invocations:Http:Enabled", "true");
                builder.UseSetting("Weave:Invocations:Http:DecisionsEnabled", "true");
                builder.UseSetting("CapabilityTokens:SigningKey", "synthetic-uuid-pilot-stable-signing-key-not-production-12345");
                builder.UseSetting("CapabilityTokens:RevocationDirectory", Path.Combine(root, "revocations"));
                builder.UseSetting("CapabilityTokens:RequireExistingStorage", recovery.ToString());
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(clock);
                    services.PostConfigure<InvocationJournalOptions>(options =>
                    {
                        options.DatabasePath = Path.Combine(root, "journal.db");
                        options.RequireExistingStorage = recovery;
                        options.ApprovalRequiredGrants = requireApproval ? ["tool:files:invoke:write_file"] : [];
                        options.ApprovalLifetime = TimeSpan.FromMinutes(5);
                    });
                });
            });
            _host.UseKestrel(0);
            Client = _host.CreateClient();
        }

        private CapabilityToken Token(string subject, HashSet<string> grants) => Tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "uuid-pilot",
            IssuedTo = subject,
            Grants = grants,
            Lifetime = TimeSpan.FromMinutes(5)
        });
        public CapabilityToken Agent() => Token("agent", ["invocation:read", "tool:files:invoke:write_file"]);
        public CapabilityToken Reader() => Token("agent", ["invocation:read", "invocation:proposal:read"]);
        public CapabilityToken Reviewer() => Token("human", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
        public async Task ConnectAsync(string? root = null)
        {
            Directory.CreateDirectory(root ?? Path.GetDirectoryName(Target)!);
            await Tool.ConnectAsync(new ToolSpec
            {
                Name = "files",
                Type = ToolType.FileSystem,
                FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = root ?? Path.GetDirectoryName(Target)! }
            }, Token("setup", ["tool:files:connect"]));
        }

        public async Task<HttpResponseMessage> Send(HttpMethod method, string suffix, object? body, CapabilityToken token)
        {
            using var request = new HttpRequestMessage(method, "/api/workspaces/uuid-pilot/tools/files/invocations" + suffix);
            request.Headers.Add("X-Weave-Capability", WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token,
                JsonSerializerOptions.Web)));
            if (body is not null)
                request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
            return await Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public long Scalar(string sql) => Database(sql, scalar: true);
        public void Execute(string sql) => Database(sql, scalar: false);
        private long Database(string sql, bool scalar)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, "journal.db"),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return scalar ? Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) : command.ExecuteNonQuery();
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.DisposeAsync();
            await _parent.DisposeAsync();
        }
    }
}
