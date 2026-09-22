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

public sealed partial class SingleHostStorageRecoveryTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weave-host-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly string _key = "test-recovery-not-production-" + Guid.NewGuid().ToString("N");
    private string Database => Path.Combine(_root, "state", "journal.db");
    private string Revocations => Path.Combine(_root, "state", "revocations");
    private string Target => Path.Combine(_root, "tools", "note.txt");

    public SingleHostStorageRecoveryTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "original");
    }

    private async Task RunHostAsync(bool recovery, bool requireApproval, Func<RunningHost, Task> run)
    {
        await using var parent = new SiloFactory();
        await using var host = parent.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Weave:Auth:Mode", "none");
            builder.UseSetting("Weave:Invocations:Http:Enabled", "true");
            builder.UseSetting("Weave:Invocations:Http:AgentOnly", "true");
            builder.UseSetting("Weave:Invocations:Http:DecisionsEnabled", "false");
            builder.UseSetting("CapabilityTokens:SigningKey", _key);
            builder.UseSetting("CapabilityTokens:RevocationDirectory", Revocations);
            builder.UseSetting("CapabilityTokens:RequireExistingStorage", recovery.ToString());
            builder.UseSetting("Weave:Invocations:RequireExistingStorage", recovery.ToString());
            builder.ConfigureServices(services => services.PostConfigure<InvocationJournalOptions>(options =>
            {
                options.DatabasePath = Database;
                options.ApprovalRequiredGrants = requireApproval ? ["tool:files:invoke:write_file"] : [];
            }));
        });
        using var client = host.CreateClient();
        // Bootstrap may initialize state; recovery startup must not depend on a later service lookup.
        if (!recovery)
            _ = host.Services.GetRequiredService<ICapabilityTokenService>();
        var running = new RunningHost(host, client, Path.GetDirectoryName(Target)!);
        await run(running);
    }

    private static ToolInvocation Request() => new()
    {
        InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
        ToolName = "files",
        Method = "write_file",
        Parameters = new() { ["path"] = "note.txt" },
        RawInput = "retained request body"
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Database,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class RunningHost(WebApplicationFactory<Program> host, HttpClient client, string toolRoot)
    {
        public HttpClient Client => client;
        public string Route { get; } = "/api/workspaces/recovery/tools/files/invocations";
        public ICapabilityTokenService Tokens => host.Services.GetRequiredService<ICapabilityTokenService>();
        public IToolActor Tool => host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IToolActor>(VirtualActorId.From("recovery/files"));
        public CapabilityToken Token(string subject, HashSet<string>? grants = null) => Tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "recovery",
            IssuedTo = subject,
            Grants = grants ?? ["tool:files:invoke:write_file", "invocation:read"],
            Lifetime = TimeSpan.FromMinutes(30)
        });
        public Task<ToolHandle> ConnectAsync() => Tool.ConnectAsync(new ToolSpec
        {
            Name = "files",
            Type = ToolType.FileSystem,
            FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = toolRoot }
        }, Token("setup", ["tool:files:connect"]));
        public async Task<HttpResponseMessage> SendAsync(ToolInvocation invocation, CapabilityToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Route);
            request.Headers.Add("X-Weave-Capability", WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions)));
            request.Content = JsonContent.Create(invocation, options: JsonOptions);
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }
    }
}
