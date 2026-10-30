using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class LocalCliCancellationProcessTests
{
    [Fact]
    public Task GuardAsync_CancelDuringCredentialRequest_StopsWithoutStatusQueryOrStateMutation() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalCliCancellationProcessTests), async root =>
        {
            await using var server = new LocalCliLoopbackServer(
                new LocalCliHttpStep("POST", "/api/operator/credentials/agent/issue", 0, ""));
            using var fixture = new LocalCliBoundaryFixture(root, server.Port);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var running = LocalCliCommand.GuardAsync(() => fixture.Command.StatusAsync(fixture.DirectoryPath,
                "82c07b3d2a3646e88f2f0b8db07c452b", cancellation.Token));
            try
            {
                await server.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
                running.IsCompleted.ShouldBeFalse();
                cancellation.Cancel();

                (await running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(1);
                fixture.Error.ToString().ShouldContain("Stopped. Query the original UUID before continuing any interrupted request.");
                fixture.Output.ToString().ShouldBeEmpty();
                server.Headers.ShouldHaveSingleItem();
                fixture.AssertPreserved();
            }
            finally
            {
                cancellation.Cancel();
                (await running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(1);
            }
        });
}
