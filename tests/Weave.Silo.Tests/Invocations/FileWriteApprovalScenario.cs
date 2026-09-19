using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

internal sealed class FileWriteApprovalScenario : IAsyncDisposable
{
    private SiloFactory? _parent;
    private WebApplicationFactory<Program>? _host;
    private HttpClient? _client;
    private readonly Clock _clock = new();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"weave-file-approval-{Guid.NewGuid():N}");
    public string DataRoot => Path.Combine(Root, "data");
    public string FilePath => Path.Combine(DataRoot, "note.txt");
    public string Database => Path.Combine(Root, "journal.db");
    public IToolActor Tool { get; private set; } = null!;
    public ICapabilityTokenService Tokens => _host!.Services.GetRequiredService<ICapabilityTokenService>();
    public CapabilityToken Writer => Mint("writer", "tool:files:connect", "tool:files:invoke:write_file", "invocation:read", "approval:read", "approval:cancel");
    public CapabilityToken Reviewer => Mint("reviewer", "approval:read", "approval:decide", "tool:files:invoke:write_file");
    public ToolSpec Spec => new()
    {
        Name = "files",
        Type = ToolType.FileSystem,
        FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = DataRoot }
    };

    public static async Task<FileWriteApprovalScenario> StartAsync()
    {
        var scenario = new FileWriteApprovalScenario();
        Directory.CreateDirectory(scenario.DataRoot);
        File.WriteAllText(scenario.FilePath, "original");
        await scenario.RestartAsync();
        return scenario;
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        _parent = new SiloFactory();
        _host = _parent.WithWebHostBuilder(builder => builder
            .UseSetting("Weave:Approvals:RequireFileWriteApproval", "true")
            .UseSetting("CapabilityTokens:SigningKey", "test-signing-key-that-is-at-least-32-chars-long")
            .UseSetting("CapabilityTokens:RevocationDirectory", Path.Combine(Root, "revocations"))
            .ConfigureServices(services =>
            {
                services.PostConfigure<InvocationJournalOptions>(o => o.DatabasePath = Database);
                services.PostConfigure<InvocationApprovalOptions>(o => o.Lifetime = TimeSpan.FromMinutes(10));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            }));
        _client = _host.CreateClient();
        Tool = _host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IToolActor>(VirtualActorId.From("approval-ws/files"));
        await Tool.ConnectAsync(Spec, Writer);
    }

    public CapabilityToken Mint(string subject, params string[] grants) => Tokens.Mint(new CapabilityTokenRequest
    {
        WorkspaceId = "approval-ws",
        IssuedTo = subject,
        Grants = [.. grants],
        Lifetime = TimeSpan.FromHours(1)
    });

    public static ToolInvocation Request() => new()
    {
        InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
        ToolName = "files",
        Method = "write_file",
        Parameters = new() { ["path"] = "note.txt" },
        RawInput = "exact approved note <<CONTENT-MARKER>>"
    };

    public async Task<FileWriteApproval> ProposeAsync(ToolInvocation request)
    {
        var pending = await Tool.InvokeAsync(request, Writer);
        pending.ErrorCode.ShouldBe("approval-required");
        pending.ApprovalState.ShouldBe(ApprovalState.Pending);
        pending.AttemptId.ShouldBeNull();
        File.ReadAllText(FilePath).ShouldBe("original");
        return (await Tool.GetApprovalAsync(pending.InvocationId!.Value, Reviewer)).ShouldNotBeNull();
    }

    public void Advance(TimeSpan duration) => _clock.Now += duration;

    public long Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task StopAsync()
    {
        _client?.Dispose();
        if (_host is not null)
            await _host.DisposeAsync();
        if (_parent is not null)
            await _parent.DisposeAsync();
        _host = null;
        _parent = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = TimeProvider.System.GetUtcNow();
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
