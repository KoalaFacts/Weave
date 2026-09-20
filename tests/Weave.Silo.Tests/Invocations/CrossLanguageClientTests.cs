using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class CrossLanguageClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("typescript", "rust")]
    [InlineData("rust", "typescript")]
    public async Task Invoke_CrossLanguageApprovalAndResume_PreservesInputsAuthorityAndSingleEffect(string sender, string resumer)
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "protocol", "governed-tools", "requests.json")))!.AsArray();
        foreach (var vector in vectors.Skip(1))
        {
            var read = vector!["request"]!.DeepClone().AsObject();
            read["invocationId"] = Guid.NewGuid().ToString("N");
            var reply = await fx.CallAsync(sender, "invoke", read, fx.Reader());
            reply.GetProperty("status").GetInt32().ShouldBe(200);
            reply.GetProperty("body").GetProperty("output").GetString().ShouldBe("original");
        }
        var write = vectors[0]!["request"]!.DeepClone().AsObject();
        write["invocationId"] = Guid.NewGuid().ToString("N");
        var id = write["invocationId"]!.GetValue<string>();
        var denied = await fx.CallAsync(sender, "invoke", write, fx.Reader());
        denied.GetProperty("status").GetInt32().ShouldBe(403);
        File.ReadAllText(fx.Target).ShouldBe("original");
        var pending = await fx.CallAsync(sender, "invoke", write, fx.Writer());
        pending.GetProperty("status").GetInt32().ShouldBe(202);
        pending.GetProperty("body").TryGetProperty("attemptId", out _).ShouldBeFalse();
        File.ReadAllText(fx.Target).ShouldBe("original");
        var status = await fx.CallAsync(resumer, "approval", write, fx.Writer());
        status.GetProperty("body").GetProperty("approvalState").GetString().ShouldBe("Pending");
        var absent = await fx.CallAsync(resumer, "status", write, fx.Writer());
        absent.GetProperty("status").GetInt32().ShouldBe(404);

        // The TypeScript operator entry uses a different credential and the actual reviewed-decision API.
        var preview = await fx.OperatorAsync("review", write);
        preview.GetProperty("status").GetInt32().ShouldBe(200);
        preview.GetProperty("body").GetProperty("rawInput").GetString().ShouldBe(write["rawInput"]!.GetValue<string>());
        var digest = preview.GetProperty("body").GetProperty("planDigest").GetString();
        var approved = await fx.OperatorAsync("decide", new JsonObject
        {
            ["invocation"] = write.DeepClone(),
            ["planDigest"] = digest,
            ["decision"] = "approve"
        });
        approved.GetProperty("status").GetInt32().ShouldBe(200);
        File.ReadAllText(fx.Target).ShouldBe("original");
        var executed = await fx.CallAsync(resumer, "invoke", write, fx.Writer());
        executed.GetProperty("status").GetInt32().ShouldBe(200);
        executed.GetProperty("body").GetProperty("outcome").GetString().ShouldBe("Succeeded");
        File.ReadAllText(fx.Target).ShouldBe(write["rawInput"]!.GetValue<string>());
        var changed = write.DeepClone().AsObject();
        changed["rawInput"] = "different content";
        (await fx.CallAsync(sender, "invoke", changed, fx.Writer())).GetProperty("status").GetInt32().ShouldBe(409);
        File.WriteAllText(fx.Target, "later external change");
        var repeated = await fx.CallAsync(sender, "invoke", write, fx.Writer());
        repeated.GetProperty("body").GetProperty("isReplay").GetBoolean().ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("later external change");
        var foreign = fx.Token("other", ["invocation:read"]);
        (await fx.CallAsync(resumer, "status", write, foreign)).GetProperty("status").GetInt32().ShouldBe(404);
        var found = await fx.CallAsync(resumer, "status", write, fx.Writer());
        found.GetProperty("body").GetProperty("invocationId").GetString().ShouldBe(id);
    }

    [Theory]
    [InlineData("typescript", "rust")]
    [InlineData("rust", "typescript")]
    public async Task Invoke_JournalFailure_PreservesNotDispatchedAndUnknownWithoutReplay(string sender, string observer)
    {
        await using var fx = new Fixture(requireApproval: false);
        await fx.ConnectAsync();
        var write = new JsonObject
        {
            ["invocationId"] = Guid.NewGuid().ToString("N"),
            ["toolName"] = "files",
            ["method"] = "write_file",
            ["parameters"] = new JsonObject { ["path"] = "note.txt" },
            ["rawInput"] = "effect"
        };
        fx.Sql("CREATE TRIGGER fail_admission BEFORE INSERT ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'private admission detail'); END;");
        var notSent = await fx.CallAsync(sender, "invoke", write, fx.Writer());
        notSent.GetProperty("status").GetInt32().ShouldBe(503);
        notSent.GetProperty("body").GetProperty("outcome").GetString().ShouldBe("NotDispatched");
        File.ReadAllText(fx.Target).ShouldBe("original");
        fx.Sql("DROP TRIGGER fail_admission;");
        fx.Sql("CREATE TRIGGER fail_completion BEFORE UPDATE ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'private completion detail'); END;");
        var unknown = await fx.CallAsync(sender, "invoke", write, fx.Writer());
        unknown.GetProperty("status").GetInt32().ShouldBe(409);
        unknown.GetProperty("body").GetProperty("outcome").GetString().ShouldBe("OutcomeUnknown");
        File.ReadAllText(fx.Target).ShouldBe("effect");
        File.WriteAllText(fx.Target, "external change");
        var observed = await fx.CallAsync(observer, "status", write, fx.Writer());
        observed.GetProperty("status").GetInt32().ShouldBe(200);
        observed.GetProperty("body").GetProperty("outcome").GetString().ShouldBe("OutcomeUnknown");
        var repeated = await fx.CallAsync(observer, "invoke", write, fx.Writer());
        repeated.GetProperty("status").GetInt32().ShouldBe(409);
        File.ReadAllText(fx.Target).ShouldBe("external change");
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Weave.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The checked-out client sources are required.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-language-clients-{Guid.NewGuid():N}");
        private readonly SiloFactory _parent = new();
        private readonly WebApplicationFactory<Program> _host;
        private readonly HttpClient _client;
        public string Target => Path.Combine(_root, "tools", "note.txt");
        private string Database => Path.Combine(_root, "journal.db");
        private string Address { get; }
        private ICapabilityTokenService Tokens => _host.Services.GetRequiredService<ICapabilityTokenService>();
        private IToolActor Tool => _host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IToolActor>(VirtualActorId.From("workspace/files"));

        public Fixture(bool requireApproval)
        {
            Directory.CreateDirectory(Path.Combine(_root, "tools"));
            File.WriteAllText(Target, "original");
            _host = _parent.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Weave:Auth:Mode", "none");
                builder.UseSetting("Weave:Invocations:Http:Enabled", "true");
                builder.UseSetting("Weave:Invocations:Http:DecisionsEnabled", "true");
                builder.UseSetting("CapabilityTokens:SigningKey", "test-cross-language-" + Guid.NewGuid().ToString("N"));
                builder.UseSetting("CapabilityTokens:RevocationDirectory", Path.Combine(_root, "revocations"));
                builder.ConfigureServices(services => services.PostConfigure<InvocationJournalOptions>(options =>
                {
                    options.DatabasePath = Database;
                    options.ApprovalRequiredGrants = requireApproval ? ["tool:files:invoke:write_file"] : [];
                }));
            });
            _host.UseKestrel(0);
            _client = _host.CreateClient();
            Address = new UriBuilder(_client.BaseAddress!) { Host = "127.0.0.1" }.Uri.ToString();
        }
        public Task<ToolHandle> ConnectAsync() => Tool.ConnectAsync(new ToolSpec
        {
            Name = "files",
            Type = ToolType.FileSystem,
            FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = Path.GetDirectoryName(Target)! }
        }, Token("setup", ["tool:files:connect"]));
        public CapabilityToken Token(string subject, HashSet<string> grants) => Tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "workspace",
            IssuedTo = subject,
            Grants = grants,
            Lifetime = TimeSpan.FromMinutes(5)
        });
        public CapabilityToken Reader() => Token("agent", ["tool:files:invoke:read_file", "invocation:read"]);
        public CapabilityToken Writer() => Token("agent", ["tool:files:invoke:write_file", "invocation:read"]);
        public Task<JsonElement> OperatorAsync(string operation, JsonObject body) => RunAsync("operator", operation, "", body,
            Token("reviewer", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]));
        public Task<JsonElement> CallAsync(string language, string operation, JsonObject body, CapabilityToken token) =>
            RunAsync(language, operation, body["invocationId"]!.GetValue<string>(), body, token);

        private async Task<JsonElement> RunAsync(string language, string operation, string id, JsonObject body, CapabilityToken token)
        {
            var repo = RepositoryRoot();
            var isRust = language == "rust";
            var binary = Path.Combine(repo, "clients", "rust", "target", "debug", OperatingSystem.IsWindows() ? "weave-client.exe" : "weave-client");
            var start = new ProcessStartInfo(isRust ? binary : "node")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!isRust)
                start.ArgumentList.Add(Path.Combine(repo, "tests", "client-probes", language + ".mjs"));
            start.ArgumentList.Add(operation);
            start.ArgumentList.Add(Address);
            start.ArgumentList.Add("workspace");
            if (language != "operator")
            {
                start.ArgumentList.Add("files");
                start.ArgumentList.Add(id);
            }
            start.Environment["WEAVE_CAPABILITY"] = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions));
            start.Environment.Remove("WEAVE_GLOBAL_BEARER");
            start.Environment.Remove("WEAVE_REQUEST_TIMEOUT_MS");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var process = new Process { StartInfo = start };
            process.Start().ShouldBeTrue();
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.StandardInput.WriteAsync(body.ToJsonString().AsMemory(), deadline.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token);
                var output = await stdout;
                var error = await stderr;
                process.ExitCode.ShouldBe(0, $"{language} client: {error}");
                error.ShouldBeEmpty();
                output.ShouldNotContain(token.Signature);
                using var json = JsonDocument.Parse(output);
                return json.RootElement.Clone();
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cleanup.Token);
                }
            }
        }
        public void Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _host.DisposeAsync();
            await _parent.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }
}
