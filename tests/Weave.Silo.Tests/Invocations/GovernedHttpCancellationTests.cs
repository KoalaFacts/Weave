using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class GovernedHttpCancellationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("invoke")]
    [InlineData("outcome")]
    [InlineData("approval")]
    [InlineData("review")]
    public async Task SendAsync_CancelledWhileGrainAuthorizes_ObservesCancellationBeforeAdmission(string operation)
    {
        var root = Path.Combine(Path.GetTempPath(), $"weave-http-cancellation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        var target = Path.Combine(root, "tools", "note.txt");
        File.WriteAllText(target, "original");
        var gate = new AuthorizationGate();
        try
        {
            await using var parent = new SiloFactory();
            await using var host = parent.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Weave:Auth:Mode", "none");
                builder.UseSetting("Weave:Invocations:Http:Enabled", "true");
                builder.UseSetting("CapabilityTokens:SigningKey", "test-cancellation-" + Guid.NewGuid().ToString("N"));
                builder.UseSetting("CapabilityTokens:RevocationDirectory", Path.Combine(root, "revocations"));
                builder.UseSetting("Weave:Invocations:DatabasePath", Path.Combine(root, "journal.db"));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ICapabilityAuthorizer>();
                    services.AddSingleton<ICapabilityAuthorizer>(provider => new GatedAuthorizer(
                        new CapabilityAuthorizer(provider.GetRequiredService<ICapabilityTokenService>(),
                            provider.GetRequiredService<IEventBus>(), NullLogger<CapabilityAuthorizer>.Instance), gate));
                });
            });
            using var client = host.CreateClient();
            var tokens = host.Services.GetRequiredService<ICapabilityTokenService>();
            var tool = host.Services.GetRequiredService<IVirtualActorProvider>()
                .GetActor<IToolActor>(VirtualActorId.From("workspace/files"));
            CapabilityToken Token(string subject, HashSet<string> grants) => tokens.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "workspace",
                IssuedTo = subject,
                Grants = grants,
                Lifetime = TimeSpan.FromMinutes(5)
            });
            await tool.ConnectAsync(new ToolSpec
            {
                Name = "files",
                Type = ToolType.FileSystem,
                FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = Path.Combine(root, "tools") }
            }, Token("setup", ["tool:files:connect"]));
            var id = InvocationId.From(Guid.NewGuid().ToString("N"));
            var route = "/api/workspaces/workspace/tools/files/invocations";
            var suffix = operation switch
            {
                "invoke" => "",
                "approval" => "/" + id + "/approval",
                "review" => "/" + id + "/approval/review",
                _ => "/" + id
            };
            var hasBody = operation is "invoke" or "review";
            using var message = new HttpRequestMessage(hasBody ? HttpMethod.Post : HttpMethod.Get, route + suffix);
            var credential = Token("cancellation-probe", ["tool:files:invoke:write_file", "invocation:read"]);
            message.Headers.Add("X-Weave-Capability", WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions)));
            if (hasBody)
                message.Content = JsonContent.Create(new ToolInvocation
                {
                    InvocationId = id,
                    ToolName = "files",
                    Method = "write_file",
                    Parameters = new() { ["path"] = "note.txt" },
                    RawInput = "must not be written"
                }, options: JsonOptions);
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var pending = client.SendAsync(message, abort.Token);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                await abort.CancelAsync();
                await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
                // This observation is inside ToolActor's real Grain call, not merely the HTTP client task.
                await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            finally
            {
                gate.Release.TrySetResult();
                await abort.CancelAsync();
                try
                {
                    using var response = await pending.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                }
                catch (OperationCanceledException) when (abort.IsCancellationRequested)
                {
                    // The client request was deliberately cancelled; still drain its completion.
                }
            }
            // The non-reentrant grain processes this barrier after the cancelled operation exits.
            await tool.GetHandleAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            File.ReadAllText(target).ShouldBe("original");
            host.Services.GetRequiredService<IInvocationJournal>()
                .Find("workspace", id, TestContext.Current.CancellationToken).ShouldBeNull();
        }
        finally
        {
            gate.Release.TrySetResult();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class AuthorizationGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedAuthorizer(ICapabilityAuthorizer inner, AuthorizationGate gate) : ICapabilityAuthorizer
    {
        private int _entered;

        public async Task AuthorizeAsync(CapabilityToken token, string grant, string? actorWorkspaceId,
            [CallerMemberName] string actionContext = "")
        {
            await inner.AuthorizeAsync(token, grant, actorWorkspaceId, actionContext);
            if (token.IssuedTo != "cancellation-probe" || Interlocked.Exchange(ref _entered, 1) != 0)
                return;
            using var registration = token.CancellationToken.Register(() => gate.Cancelled.TrySetResult());
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }
}
