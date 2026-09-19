using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class FileWriteApprovalAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_ServerRequiresApproval_DoesNotWriteOrCreateAttempt(bool provideId)
    {
        var root = Path.Combine(Path.GetTempPath(), $"weave-approval-admission-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "note.txt");
        File.WriteAllText(path, "original");
        try
        {
            await using var parent = new SiloFactory();
            await using var host = parent.WithWebHostBuilder(builder =>
                builder.UseSetting("Weave:Approvals:RequireFileWriteApproval", "true"));
            using var client = host.CreateClient();
            host.Services.GetRequiredService<IConfiguration>()["Weave:Approvals:RequireFileWriteApproval"].ShouldBe("true");
            var tokens = host.Services.GetRequiredService<ICapabilityTokenService>();
            var token = tokens.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "approval-workspace",
                IssuedTo = "writer",
                Grants = ["tool:files:connect", "tool:files:invoke:write_file", "invocation:read"],
                Lifetime = TimeSpan.FromMinutes(5)
            });
            var tool = host.Services.GetRequiredService<IVirtualActorProvider>()
                .GetActor<IToolActor>(VirtualActorId.From("approval-workspace/files"));
            await tool.ConnectAsync(new ToolSpec
            {
                Name = "files",
                Type = ToolType.FileSystem,
                FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = root }
            }, token);
            var result = await tool.InvokeAsync(new ToolInvocation
            {
                InvocationId = provideId ? InvocationId.From(Guid.NewGuid().ToString("N")) : null,
                ToolName = "files",
                Method = "write_file",
                Parameters = new() { ["path"] = "note.txt" },
                RawInput = "approved content only"
            }, token);

            File.ReadAllText(path).ShouldBe("original", "Pending approval must not cause the filesystem effect.");
            result.Success.ShouldBeFalse();
            result.ErrorCode.ShouldBe("approval-required");
            result.InvocationId.ShouldNotBeNull();
            result.AttemptId.ShouldBeNull("Waiting for a person is not an execution attempt.");
            result.Outcome.ShouldBeNull("Awaiting approval is not an unknown dispatched outcome.");
            (await tool.GetInvocationAsync(result.InvocationId!.Value, token)).ShouldBeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
