using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class ApprovalHostRestartTests
{
    [Fact]
    public async Task InvokeAsync_HostRestartsWhileWaiting_ApprovalAndSingleExecutionSurvive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"weave-approval-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "note.txt");
        File.WriteAllText(path, "original");
        var database = Path.Combine(root, "journal.db");
        var workspace = "ws-" + Guid.NewGuid().ToString("N");
        var id = InvocationId.From(Guid.NewGuid().ToString("N"));
        var request = new ToolInvocation
        {
            InvocationId = id,
            ToolName = "files",
            Method = "write_file",
            Parameters = new() { ["path"] = "note.txt" },
            RawInput = "approved private body"
        };
        string planDigest;
        try
        {
            await using (var parent = new SiloFactory())
            await using (var host = Configure(parent, database))
            {
                using var client = host.CreateClient();
                var tool = Tool(host, workspace);
                var writer = Token(host, workspace, "writer", ["tool:files:connect", "tool:files:invoke:write_file", "invocation:read"]);
                await tool.ConnectAsync(Spec(root), writer);
                var pending = await tool.InvokeAsync(request, writer);
                pending.ErrorCode.ShouldBe("approval-pending");
                pending.AttemptId.ShouldBeNull();
                var approval = (await tool.GetApprovalAsync(id, writer)).ShouldNotBeNull();
                approval.State.ShouldBe(InvocationApprovalState.Pending);
                planDigest = approval.PlanDigest;
                JsonSerializer.Serialize(approval).ShouldNotContain("approved private body");
                File.ReadAllText(path).ShouldBe("original");
            }

            await using (var parent = new SiloFactory())
            await using (var host = Configure(parent, database))
            {
                using var client = host.CreateClient();
                var tool = Tool(host, workspace);
                (await tool.GetHandleAsync()).ShouldBeNull();
                var approver = Token(host, workspace, "operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
                var approval = (await tool.GetApprovalAsync(id, approver)).ShouldNotBeNull();
                approval.PlanDigest.ShouldBe(planDigest);
                (await tool.DecideApprovalAsync(id, planDigest, InvocationApprovalDecision.Approve, approver)).Succeeded.ShouldBeTrue();
                File.ReadAllText(path).ShouldBe("original");
                var writer = Token(host, workspace, "writer", ["tool:files:connect", "tool:files:invoke:write_file", "invocation:read"]);
                await tool.ConnectAsync(Spec(root), writer);
                var result = await tool.InvokeAsync(request, writer);
                result.Success.ShouldBeTrue(result.Error);
                result.ApprovalState.ShouldBe(InvocationApprovalState.Consumed);
                File.ReadAllText(path).ShouldBe("approved private body");
                File.WriteAllText(path, "changed after execution");
                (await tool.InvokeAsync(request, writer)).IsReplay.ShouldBeTrue();
                File.ReadAllText(path).ShouldBe("changed after execution");
                (await tool.GetInvocationAsync(id, writer)).ShouldNotBeNull().Attempt.Outcome.ShouldBe(InvocationOutcome.Succeeded);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WebApplicationFactory<Program> Configure(SiloFactory parent, string database) =>
        parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<InvocationJournalOptions>(options =>
            {
                options.DatabasePath = database;
                options.ApprovalRequiredGrants = ["tool:files:invoke:write_file"];
                options.ApprovalLifetime = TimeSpan.FromMinutes(5);
            })));

    private static IToolActor Tool(WebApplicationFactory<Program> host, string workspace) =>
        host.Services.GetRequiredService<IVirtualActorProvider>().GetActor<IToolActor>(VirtualActorId.From(workspace + "/files"));

    private static CapabilityToken Token(WebApplicationFactory<Program> host, string workspace, string subject, HashSet<string> grants) =>
        host.Services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspace,
            IssuedTo = subject,
            Grants = grants,
            Lifetime = TimeSpan.FromMinutes(5)
        });

    private static ToolSpec Spec(string root) => new()
    {
        Name = "files",
        Type = ToolType.FileSystem,
        FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = root }
    };
}
