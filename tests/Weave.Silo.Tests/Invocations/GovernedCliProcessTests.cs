using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tests.Processes;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests.Invocations;

public sealed class GovernedCliProcessTests
{
    [Fact]
    public async Task InvokeAsync_OutputLimitAfterEffect_PreservesUnknownAcrossRestartWithoutReplay()
    {
        using var child = new ProcessTestChild("effect-overflow");
        var journalRoot = Path.Join(Path.GetTempPath(), $"weave-cli-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(journalRoot);
        var workspace = "process-" + Guid.NewGuid().ToString("N");
        var request = child.Invocation with { InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")) };
        InvocationAttemptId? admittedAttempt = null;
        try
        {
            for (var restart = 0; restart < 2; restart++)
            {
                await using var parent = new SiloFactory();
                await using var host = parent.WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("Weave:Auth:Mode", "none");
                    builder.UseSetting("CapabilityTokens:SigningKey", "test-process-" + Guid.NewGuid().ToString("N"));
                    builder.UseSetting("CapabilityTokens:RevocationDirectory", journalRoot);
                    builder.ConfigureServices(services => services.PostConfigure<InvocationJournalOptions>(options =>
                        options.DatabasePath = Path.Join(journalRoot, "invocations.db")));
                });
                using var client = host.CreateClient();
                var tokens = host.Services.GetRequiredService<ICapabilityTokenService>();
                var token = tokens.Mint(new CapabilityTokenRequest
                {
                    WorkspaceId = workspace,
                    IssuedTo = "process-test-agent",
                    Grants = ["tool:process-test:connect", "tool:process-test:invoke:exec", "invocation:read"],
                    Lifetime = TimeSpan.FromMinutes(5)
                }) with
                { CancellationToken = TestContext.Current.CancellationToken };
                var actor = host.Services.GetRequiredService<IVirtualActorProvider>()
                    .GetActor<IToolActor>(VirtualActorId.From(workspace + "/process-test"));
                await actor.ConnectAsync(new ToolSpec
                {
                    Name = "process-test",
                    Type = ToolType.Cli,
                    Cli = new CliConfig
                    {
                        Shell = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh",
                        AllowedCommands = [request.RawInput!]
                    }
                }, token, TestContext.Current.CancellationToken);

                var result = await actor.InvokeAsync(request, token);

                result.Success.ShouldBeFalse();
                result.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
                result.OutcomeRecorded.ShouldBeTrue();
                result.IsReplay.ShouldBe(restart == 1);
                if (restart == 0)
                {
                    result.ErrorCode.ShouldBe("process-output-limit");
                    admittedAttempt = result.AttemptId;
                    admittedAttempt.ShouldNotBeNull();
                }
                else
                    result.AttemptId.ShouldBe(admittedAttempt);
                File.ReadAllText(Path.Join(child.Root, "effect")).ShouldBe("effect");
                var retained = (await actor.GetInvocationAsync(request.InvocationId!.Value, token)).ShouldNotBeNull();
                retained.Attempt.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
                retained.Attempt.AttemptId.ShouldBe(admittedAttempt!.Value);
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(journalRoot))
                File.Delete(file);
            Directory.Delete(journalRoot);
        }
    }
}
