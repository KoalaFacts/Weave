using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class HostProcessRecoveryTests
{
    // The same test assembly is an isolated worker, not a new product executable.
    private static async Task RunChildAsync(string root)
    {
        Path.IsPathFullyQualified(root).ShouldBeTrue();
        var settings = JsonSerializer.Deserialize<WorkerSettings>(File.ReadAllText(Path.Combine(root, "worker.json")), JsonOptions)!;
        await using var parent = new SiloFactory();
        await using var host = parent.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Weave:Auth:Mode", "none");
            builder.UseSetting("Weave:Invocations:Http:Enabled", "true");
            builder.UseSetting("Weave:Invocations:Http:AgentOnly", "true");
            builder.UseSetting("Weave:Invocations:Http:DecisionsEnabled", "false");
            builder.UseSetting("CapabilityTokens:SigningKey", settings.SigningKey);
            builder.UseSetting("CapabilityTokens:RevocationDirectory", Path.Combine(root, "state", "revocations"));
            builder.UseSetting("CapabilityTokens:RequireExistingStorage", settings.Recovery.ToString());
            builder.UseSetting("Weave:Invocations:RequireExistingStorage", settings.Recovery.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(settings.Now));
                services.RemoveAll<IToolConnector>();
                services.AddSingleton<IToolConnector>(new CompletionGateConnector(root, settings.HoldCompletion));
                services.PostConfigure<InvocationJournalOptions>(options =>
                {
                    options.DatabasePath = Path.Combine(root, "state", "journal.db");
                    options.ApprovalRequiredGrants = [];
                });
            });
        });
        host.UseKestrel(0);
        using var client = host.CreateClient();
        var tokens = host.Services.GetRequiredService<ICapabilityTokenService>();
        var tool = host.Services.GetRequiredService<IVirtualActorProvider>().GetActor<IToolActor>(VirtualActorId.From("lifetime/files"));
        await tool.ConnectAsync(new ToolSpec
        {
            Name = "files",
            Type = ToolType.FileSystem,
            FileSystem = new FileSystemToolConfig { Root = Path.Combine(root, "tools") }
        }, tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "lifetime",
            IssuedTo = "setup",
            Grants = ["tool:files:connect"],
            Lifetime = TimeSpan.FromMinutes(30)
        }));
        var address = new UriBuilder(client.BaseAddress!) { Host = "127.0.0.1" }.Uri.ToString();
        File.WriteAllText(Path.Combine(root, "ready.tmp"), address);
        File.Move(Path.Combine(root, "ready.tmp"), Path.Combine(root, "ready"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(100));
        (await Console.In.ReadLineAsync(deadline.Token)).ShouldBe("stop");
        // The using scopes stop actual Kestrel/Orleans; the runner then exits.
    }

    // Hold only AFTER the real filesystem effect. A kill then prevents normal completion recording.
    private sealed class CompletionGateConnector(string root, bool holdCompletion) : IToolConnector
    {
        private readonly FileSystemToolConnector _inner = new(NullLogger<FileSystemToolConnector>.Instance);
        public ToolType ToolType => _inner.ToolType;
        public ToolInvocation NormalizeInvocation(ToolInvocation invocation) => _inner.NormalizeInvocation(invocation);
        public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default) =>
            _inner.ConnectAsync(tool, token, ct);
        public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default) => _inner.DisconnectAsync(handle, ct);
        public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default) =>
            _inner.DiscoverSchemaAsync(handle, ct);
        public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
        {
            File.AppendAllText(Path.Combine(root, "dispatch-count"), "dispatch\n");
            var result = await _inner.InvokeAsync(handle, invocation, ct);
            if (holdCompletion && result.Success)
            {
                File.WriteAllText(Path.Combine(root, "effect-ready"), "written");
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return result;
        }
    }
}
